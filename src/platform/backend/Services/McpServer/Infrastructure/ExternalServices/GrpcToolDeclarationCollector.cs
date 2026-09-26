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
// ■ 🔴 **失敗の畳み方は REST と同じ**（`HttpToolDeclarationSource.CollectOneAsync`）。
//   全 status・s2s トークン取得失敗・期限切れ・空の申告を「申告なし」（null）へ畳む ——
//   申告の無いツールは公開構成が要求していても公開されない（ADR-0024 §5「推測で公開しない」）。
//   **移行の不変条件は「挙動を変えない」である。**
//   ただしログは分ける: `UNAUTHENTICATED` / `PERMISSION_DENIED` と s2s トークンの取得失敗は**配線不備**
//   （service account・`platform-service`・`ServiceToken` の注入漏れ。再起動では直らない）なので Error。
//   REST には無かった失敗の種類であり、Warning に混ぜると「一過性の到達不能」に紛れる。
//   取得失敗は CallCredentials の中で起き、gRPC クライアントが例外を包み直すので型では見分けられない ——
//   発行側を包んで印を付け（`ServiceTokenAcquisitionException`）、例外の連鎖から印を探す（経路 ⑤ と同じ手）。
//
// ■ 期限（deadline）: REST の HttpClient（`HttpToolDeclarationSource.HttpClientName`）の `Timeout` を**そのまま引く**。
//   既定の無期限のまま 1 宛先が応答しないと、収集の 1 周が止まる（公開構成の突合も止まる）。
// ■ リトライ: 持たない（REST も持たない）。次の周期が再試行である。
// ■ 取り消し: 呼び出し側の ct による取り消し（停止要求）だけを外へ出す（REST と同じ）。
//
// チャネルは宛先アドレスごとに 1 本を使い回す（HTTP/2 は多重化される。周期ごとに張り直さない）。
public sealed class GrpcToolDeclarationCollector(
    IServiceTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<GrpcToolDeclarationCollector> logger) : IDisposable
{
    public const string GrpcServicesSection = "Mcp:GrpcServices";

    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.Ordinal);

    // service 名 → 申告の **gRPC（h2c）アドレス**（例: "document-service": "http://document-service:8081"）。
    // 🔴 **宛先ごとの opt-in である。** ここに在る宛先だけが gRPC で収集され、無い宛先は `Mcp:Services` の REST のまま。
    // 値が空の項目は構成されていないものとして扱う。
    public static IReadOnlyDictionary<string, string> ConfiguredTargets(IConfiguration configuration) =>
        configuration.GetSection(GrpcServicesSection).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.Ordinal);

    public async Task<ServiceToolDeclarations?> CollectOneAsync(string service, string address, CancellationToken ct)
    {
        try
        {
            var channel = _channels.GetOrAdd(address, CreateChannel);
            var client = new Pb.McpToolDeclarations.McpToolDeclarationsClient(channel);
            // HttpClient の無期限（InfiniteTimeSpan = -1ms）は gRPC でも無期限（REST と同じ意味）に写す。
            var timeout = Timeout();
            DateTime? deadline = timeout == System.Threading.Timeout.InfiniteTimeSpan
                ? null
                : DateTime.UtcNow.Add(timeout);
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
        catch (Exception ex) when (FindTokenFailure(ex) is { } tokenFailure)
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

    // proto → McpServer の DTO（ワイヤ形式の 6 項目は 1 対 1。名前は REST の JSON と同じ綴り）。
    public static ServiceToolDeclarations ToDto(Pb.ServiceToolDeclarations declared) =>
        new(declared.Service,
            declared.Tools
                .Select(t => new McpToolDeclaration(
                    t.Name, t.Description, t.InputSchema, t.Endpoint, t.RequiredScope, t.EgressClass))
                .ToList());

    // REST の期限と同じ値（名前付きクライアントの Timeout。未構成なら HttpClient の既定 100 秒）。
    private TimeSpan Timeout() =>
        httpClientFactory.CreateClient(HttpToolDeclarationSource.HttpClientName).Timeout;

    private GrpcChannel CreateChannel(string address) =>
        GrpcClientExtensions.CreatePlatformChannel(address, new MarkingTokenProvider(tokenProvider));

    // 例外の連鎖（InnerException と RpcException.Status.DebugException）から取得失敗の印を探す。
    private static ServiceTokenAcquisitionException? FindTokenFailure(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++)
        {
            if (ex is ServiceTokenAcquisitionException marked)
                return marked;
            ex = ex is RpcException { Status.DebugException: { } debug } ? debug : ex.InnerException;
        }
        return null;
    }

    // 発行側の失敗に印を付ける（取り消しは印を付けずにそのまま通す）。
    private sealed class MarkingTokenProvider(IServiceTokenProvider inner) : IServiceTokenProvider
    {
        public async ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            try
            {
                return await inner.GetTokenAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ServiceTokenAcquisitionException(ex);
            }
        }
    }

    private sealed class ServiceTokenAcquisitionException(Exception inner)
        : Exception("s2s トークンの取得に失敗しました。", inner);

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }
}
