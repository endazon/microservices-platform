using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using McpServer.Domain;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, NFR-09, NFR-16, ADR-0024 §2・§5, ADR-0029, ADR-0075, IADR-0379 決定 4・5,
// IADR-0462（2026-09-26 追記 / #1515, #1255 経路 ④-a）: 1 宛先ぶんのツール申告を **gRPC（h2c）** で収集する。
// 形は構成情報 API の `GrpcServiceIntrospectionCollector`（経路 ⑤）と同じである。
//
// ■ 資格情報: MCP サーバー自身の s2s トークン（realm の `mcp-server` client）を CallCredentials で付ける。
//   利用者のトークンは載せない（この経路は起動時＋定期の背景処理であり、利用者文脈を 1 バイトも持たない）。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］**申告の収集は本クラスだけで行う**（REST `GET /internal/mcp-tools` の収集 `HttpToolDeclarationSource` は撤去した。#1517）。
// 宛先は `Mcp:Services`（service 名 → h2c のアドレス）。旧キー `Mcp:GrpcServices` は撤去し、残っていれば起動を止める。
//
// ■ 🔴 **失敗の畳み方は撤去した REST と同じ**（旧 `HttpToolDeclarationSource.CollectOneAsync`）。
//   全 status・s2s トークン取得失敗・期限切れ・空の申告を「申告なし」（null）へ畳む ——
//   申告の無いツールは公開構成が要求していても公開されない（ADR-0024 §5「推測で公開しない」）。
//   **移行の不変条件は「挙動を変えない」である。**
//   ただしログは分ける: `UNAUTHENTICATED` / `PERMISSION_DENIED` と s2s トークンの取得失敗は**配線不備**
//   （service account・`platform-service`・`ServiceToken` の注入漏れ。再起動では直らない）なので Error。
//   REST には無かった失敗の種類であり、Warning に混ぜると「一過性の到達不能」に紛れる。
//   取得失敗は CallCredentials の中で起き、gRPC クライアントが例外を包み直すので型では見分けられない ——
//   発行側を包んで印を付け、例外の連鎖から印を探す（`ServiceTokenFailures`。経路 ⑤ と同じ手）。
//
// ■ 期限（deadline）: `Mcp:DeclarationTimeoutSeconds`（既定 10 秒、1 未満は 1）。常に有限である。
//   既定の無期限のまま 1 宛先が応答しないと、収集の 1 周が止まる（公開構成の突合も止まる）。
// ■ リトライ: 持たない（REST も持たない）。次の周期が再試行である。
// ■ 取り消し: 呼び出し側の ct による取り消し（停止要求）だけを外へ出す（REST と同じ）。
//
// チャネルは宛先アドレスごとに 1 本を使い回す（HTTP/2 は多重化される。周期ごとに張り直さない）。
// 収集は逐次なので `GetOrAdd` の生成関数が競合して余分なチャネルを作ることは無い。並列化するなら
// `Lazy<GrpcChannel>` で包むこと（競合に負けた側のチャネルは辞書に入らず Dispose もされない）。
public sealed class GrpcToolDeclarationCollector(
    IServiceTokenProvider tokenProvider,
    IConfiguration configuration,
    ILogger<GrpcToolDeclarationCollector> logger) : IDisposable
{
    // service 名 → 申告元サービスの **gRPC（h2c）アドレス**（例: "document-service": "http://document-service:8081"）。
    // 申告の収集とツールの実行（`GrpcToolInvoker`）が同じ宛先を引く。
    // ［2026-10-10 / #1255・[[IADR-0533]]］従前の値は REST のベース URL（`:8080`）であり、gRPC の宛先は別キー `Mcp:GrpcServices` が持っていた。
    // REST の収集を撤去したので、このキーへ一本化した（値は gRPC の宛先。[[IADR-0533]] 決定 3）。
    public const string ServicesSection = "Mcp:Services";

    /// <summary>撤去した旧キー。残っていたら起動を止める（<see cref="EnsureRetiredKeyAbsent"/>）。</summary>
    public const string RetiredGrpcServicesSection = "Mcp:GrpcServices";

    // ［2026-09-26 / #1604・IADR-0462 追記］1 宛先ぶんの収集の期限（秒）。従前は REST の名前付きクライアントの `Timeout` に与え、
    // gRPC の期限はそれを引いていた（期限の出所は 1 つ）。REST を撤去したので、ここで直接読む。1 未満は 1 に丸める。
    public const string TimeoutKey = "Mcp:DeclarationTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 10;

    public static TimeSpan ConfiguredTimeout(IConfiguration configuration) =>
        TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>(TimeoutKey) ?? DefaultTimeoutSeconds));

    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout = ConfiguredTimeout(configuration);

    // 値が空の項目は構成されていないものとして扱う。構成の順序を保つ（収集は逐次）。
    public static IReadOnlyList<KeyValuePair<string, string>> ConfiguredTargetList(IConfiguration configuration) =>
        configuration.GetSection(ServicesSection).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => new KeyValuePair<string, string>(c.Key, c.Value!))
            .ToList();

    public static IReadOnlyDictionary<string, string> ConfiguredTargets(IConfiguration configuration) =>
        ConfiguredTargetList(configuration).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    // 🔴 **撤去した旧キー `Mcp:GrpcServices` が残っていたら起動を止める**（[[IADR-0533]] 決定 3）。
    // 黙って無視すると、上書き値の移し忘れ（旧 `Mcp:Services` の REST の宛先 `:8080` は h2c を話さない）が
    // 「申告なし」＝ツールが静かに消える形でしか現れない（ADR-0024 §5 は推測で公開しない）。
    public static void EnsureRetiredKeyAbsent(IConfiguration configuration)
    {
        var stale = configuration.GetSection(RetiredGrpcServicesSection).GetChildren().Select(c => c.Key).ToList();
        if (stale.Count == 0)
            return;

        throw new InvalidOperationException(
            $"構成キー {RetiredGrpcServicesSection} は撤去しました（REST の並走の退役。#1517）。"
            + $" gRPC の宛先は {ServicesSection} へ移してください（値は h2c のアドレス。例: http://document-service:8081）。"
            + $" 残っている項目: {string.Join(", ", stale)}");
    }

    public async Task<ServiceToolDeclarations?> CollectOneAsync(string service, string address, CancellationToken ct)
    {
        try
        {
            var channel = _channels.GetOrAdd(address, CreateChannel);
            var client = new Pb.McpToolDeclarations.McpToolDeclarationsClient(channel);
            // ［2026-09-27 / #1608］期限は常に有限である（`ConfiguredTimeout` は `Math.Max(1, …)` 秒）。
            var deadline = DateTime.UtcNow.Add(_timeout);
            var declared = await client.DeclareAsync(
                new Pb.DeclareMcpToolsRequest(), deadline: deadline, cancellationToken: ct);

            if (string.IsNullOrEmpty(declared.Service))
            {
                // 🔴 空の申告は「申告として無効」。REST の空応答と同じく申告なしへ落とす ——
                // 空文字のサービスとして突合へ入れると、どの公開構成にも一致しない申告が 1 件増える。
                logger.LogWarning(
                    "MCP tool declarations from {Service} at {Address} returned an empty service name over gRPC",
                    service, address);
                return null;
            }

            return ToDto(declared);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 停止要求による取り消しは申告なしへ畳まない（REST と同じ）。gRPC は取り消しを
            // RpcException(Cancelled) で表すので、呼び出し側が待つ OperationCanceledException へ揃える。
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (ServiceTokenFailures.Find(ex) is { } tokenFailure)
        {
            logger.LogError(tokenFailure.InnerException ?? tokenFailure,
                "MCP tool declarations from {Service} at {Address} could not be collected: the caller's service token "
                + "was not obtained; check ServiceToken:ClientId / ClientSecret and the token endpoint",
                service, address);
            return null;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            logger.LogError(ex,
                "MCP tool declarations from {Service} at {Address} were rejected over gRPC ({Status}); "
                + "check the caller's service account and platform-service role",
                service, address, ex.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to collect MCP tool declarations from {Service} at {Address} over gRPC", service, address);
            return null;
        }
    }

    // proto → McpServer の DTO（ワイヤ形式の 5 項目は 1 対 1。名前は撤去した REST の JSON と同じ綴り）。
    // ［2026-09-27 / #1516］旧い申告元が送る番号 4（旧 `endpoint`）は未知のフィールドとして読み飛ばされ、ここへは来ない。
    public static ServiceToolDeclarations ToDto(Pb.ServiceToolDeclarations declared) =>
        new(declared.Service,
            declared.Tools
                .Select(t => new McpToolDeclaration(
                    t.Name, t.Description, t.InputSchema, t.RequiredScope, t.EgressClass))
                .ToList());

    // 発行側の失敗には印を付ける（`ServiceTokenFailures`。実行器 `GrpcToolInvoker` と共有）。
    private GrpcChannel CreateChannel(string address) =>
        GrpcClientExtensions.CreatePlatformChannel(address, ServiceTokenFailures.Marking(tokenProvider));

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }
}
