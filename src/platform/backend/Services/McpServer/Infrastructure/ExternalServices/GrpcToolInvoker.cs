using System.Collections.Concurrent;
using Google.Protobuf.Collections;
using Grpc.Core;
using Grpc.Net.Client;
using McpServer.Domain;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, UC-08, NFR-09, NFR-16, ADR-0024 §2, ADR-0029, ADR-0075, ADR-0117 決定 1・2・4, IADR-0379 決定 4,
// IADR-0462（2026-09-27 追記 / #1516, #1255 経路 ④-b）: ツールの実行を **gRPC（h2c）** で、ツールを申告したサービスへ送る。
// 従前の `HttpToolInvoker`（申告の `endpoint` の URL へ POST）を置き換えた。**輸送の差し替えだけ**であり、本文の意味論は変えていない
// （主体・種別・属性・個人資料の除外制約・必要スコープ ＋ 引数。利用者文脈へ改めるのは #1611）。
//
// ■ 🔴 **宛先は `PublishedTool.Service`（公開構成で申告を突き合わせたサービス）と申告名だけで決める**（ADR-0117 決定 1）。
//   アドレスは申告の収集と同じ `Mcp:GrpcServices:<サービス名>`（申告元サービスの h2c アドレス）。**申告の中身から宛先を作らない** ——
//   申告に URL はもう無く、どのサービスも他のサービスを自分のツールの実行先にできない。
//
// ■ 🔴 **fail-closed**（ADR-0117 決定 4）。宛先の経路が構成されていない・実行口が無い（`UNIMPLEMENTED`。#1611 まで全宛先）・
//   期限切れ・拒否・トークン取得失敗・到達不能は、すべて `ToolExecutionUnavailableException` にして結果を返さない。
//   利用者へ返す文言は内部の宛先（サービス名・アドレス・status）を含めない。宛先はログにだけ書く。
//   ログは配線不備（`UNAUTHENTICATED` / `PERMISSION_DENIED` / s2s トークンの取得失敗。再起動では直らない）を Error、それ以外を Warning。
//
// ■ 資格情報: MCP サーバー自身の s2s トークン（realm の `mcp-server` client。申告の収集と同じ）。利用者のトークンは載せない
//   （MCP の仕様がトークンの中継を禁じている。ADR-0117 実測 6）。
// ■ 期限: `Mcp:ToolExecutionTimeoutSeconds`（既定 30 秒、1 未満は 1）。常に有限。申告の収集の期限とは別の値である
//   （収集は背景処理、実行は利用者が応答を待つ呼び出しで、下流の処理時間も違う）。
// ■ リトライ: 持たない（ツールの実行は冪等とは限らない。MCP クライアントが再試行を判断する）。
// ■ 取り消し: 呼び出し側の ct による取り消しは拒否へ畳まず OperationCanceledException で外へ出す。
//
// チャネルは宛先アドレスごとに 1 本を使い回す（HTTP/2 は多重化される）。実行は並行に来るので `Lazy` で包み、
// 競合に負けた側のチャネルが作られて捨てられることを起こさない。
public sealed class GrpcToolInvoker : IToolInvoker, IDisposable
{
    public const string TimeoutKey = "Mcp:ToolExecutionTimeoutSeconds";
    public const int DefaultTimeoutSeconds = 30;

    // 利用者へ返す文言（内部の宛先を含めない）。試験が文言で種類を見分けるので定数に置く。
    public const string NotRoutedMessage = "このツールは現在実行できません（実行先への経路が構成されていません）。";
    public const string NoExecutionPortMessage = "このツールは現在実行できません（実行先にツールの実行口がまだありません）。";
    public const string TimedOutMessage = "このツールは現在実行できません（実行先が期限内に応答しませんでした）。";
    public const string RejectedMessage = "このツールは現在実行できません（実行先がこの呼び出しを受け付けませんでした）。";
    public const string UnreachableMessage = "このツールは現在実行できません（実行先に到達できません）。";

    private readonly IConfiguration _configuration;
    private readonly IServiceTokenProvider? _tokenProvider;
    private readonly ILogger<GrpcToolInvoker> _logger;
    private readonly ConcurrentDictionary<string, Lazy<GrpcChannel>> _channels = new(StringComparer.Ordinal);

    public GrpcToolInvoker(
        IConfiguration configuration,
        ILogger<GrpcToolInvoker> logger,
        IServiceTokenProvider? tokenProvider = null)
    {
        _configuration = configuration;
        _logger = logger;
        _tokenProvider = tokenProvider;

        // 🔴 宛先が構成されているのに s2s の発行側が居ないのは登録の誤りである（申告の収集器と同じ扱い）。
        // 実行のたびに気付くのではなく、起動の時点で落とす（Program.cs が要求を受ける前に 1 度組む）。
        if (_tokenProvider is null && GrpcToolDeclarationCollector.ConfiguredTargets(configuration).Count > 0)
            throw new InvalidOperationException(
                $"{GrpcToolDeclarationCollector.GrpcServicesSection} が構成されていますが s2s トークンの発行側が登録されていません"
                + "（AddMcpToolInvoker を AddMcpToolDeclarationSources の後に呼んでいるか確かめること）。");
    }

    public static TimeSpan ConfiguredTimeout(IConfiguration configuration) =>
        TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue<int?>(TimeoutKey) ?? DefaultTimeoutSeconds));

    public async Task<McpToolResult> InvokeAsync(
        PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct)
    {
        var service = tool.Service;
        if (!GrpcToolDeclarationCollector.ConfiguredTargets(_configuration).TryGetValue(service, out var address))
        {
            _logger.LogWarning(
                "MCP tool {Tool} of {Service} was not executed: no gRPC address is configured under {Section}",
                tool.PublishedName, service, GrpcToolDeclarationCollector.GrpcServicesSection);
            throw new ToolExecutionUnavailableException(NotRoutedMessage);
        }

        var timeout = ConfiguredTimeout(_configuration);
        try
        {
            var channel = _channels.GetOrAdd(address, a => new Lazy<GrpcChannel>(() =>
                GrpcClientExtensions.CreatePlatformChannel(a, ServiceTokenFailures.Marking(_tokenProvider!)))).Value;
            var client = new Pb.McpToolExecution.McpToolExecutionClient(channel);
            var result = await client.ExecuteAsync(
                ToRequest(tool, scope, argumentsJson),
                deadline: DateTime.UtcNow.Add(timeout),
                cancellationToken: ct);
            return ToResult(result);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 呼び出し側の取り消しは拒否へ畳まない。gRPC は取り消しを RpcException(Cancelled) で表すので揃える。
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex) when (ServiceTokenFailures.Find(ex) is { } tokenFailure)
        {
            _logger.LogError(tokenFailure.InnerException ?? tokenFailure,
                "MCP tool {Tool} of {Service} at {Address} was not executed: the caller's service token was not obtained; "
                + "check ServiceToken:ClientId / ClientSecret and the token endpoint",
                tool.PublishedName, service, address);
            throw new ToolExecutionUnavailableException(RejectedMessage, ex);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            _logger.LogError(ex,
                "MCP tool {Tool} of {Service} at {Address} was rejected over gRPC ({Status}); "
                + "check the caller's service account and platform-service role",
                tool.PublishedName, service, address, ex.StatusCode);
            throw new ToolExecutionUnavailableException(RejectedMessage, ex);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented)
        {
            // #1611 まではすべての宛先がここへ来る（実行口が無い）。配線の誤りではないので Warning。
            _logger.LogWarning(
                "MCP tool {Tool} of {Service} at {Address} was not executed: the service has no tool execution port "
                + "(platform.mcp.v1.McpToolExecution is unimplemented)",
                tool.PublishedName, service, address);
            throw new ToolExecutionUnavailableException(NoExecutionPortMessage, ex);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.DeadlineExceeded)
        {
            _logger.LogWarning(
                "MCP tool {Tool} of {Service} at {Address} was not executed: no response within {Timeout}",
                tool.PublishedName, service, address, timeout);
            throw new ToolExecutionUnavailableException(TimedOutMessage, ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "MCP tool {Tool} of {Service} at {Address} was not executed over gRPC",
                tool.PublishedName, service, address);
            throw new ToolExecutionUnavailableException(UnreachableMessage, ex);
        }
    }

    // 要求の組み立て。宛先の情報は 1 バイトも載せない（宛先はチャネルのアドレスが決めている）。
    public static Pb.ExecuteMcpToolRequest ToRequest(PublishedTool tool, ToolInvocationScope scope, string argumentsJson)
    {
        var message = new Pb.ExecuteMcpToolRequest
        {
            // 申告名（公開名ではない）。受け口は自分の申告の名前で引く。
            Tool = tool.Declaration.Name,
            // 引数は MCP クライアントから来た JSON をそのまま渡す（解釈は受け口の責務）。空は空のオブジェクト。
            ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
            Scope = new Pb.McpToolInvocationScope
            {
                SubjectId = scope.SubjectId,
                SubjectKind = scope.SubjectKind,
                ExcludePrivateNote = scope.ExcludePrivateNote,
                RequiredScope = scope.RequiredScope,
            },
        };
        foreach (var (key, value) in scope.SubjectAttributes)
            message.Scope.SubjectAttributes[key] = value;
        return message;
    }

    // 応答 → 共通エンベロープ（REST の JSON と同じ意味。null を取り得るのは body / reference_url だけ）。
    public static McpToolResult ToResult(Pb.McpToolResult result) =>
        new([.. result.Documents.Select(d => new McpToolDocument(
                d.DocumentId,
                d.Title,
                ToDictionary(d.Attributes),
                d.HasBody ? d.Body : null,
                d.HasReferenceUrl ? d.ReferenceUrl : null))],
            result.TotalCount,
            result.Truncated);

    private static Dictionary<string, string> ToDictionary(MapField<string, string> map) =>
        new(map, StringComparer.Ordinal);

    public void Dispose()
    {
        foreach (var channel in _channels.Values.Where(c => c.IsValueCreated))
            channel.Value.Dispose();
        _channels.Clear();
    }
}

// FR-16, NFR-16, IADR-0462（2026-09-27 追記 / #1516）: ツールの実行器の登録。
public static class ToolInvokerExtensions
{
    // 🔴 `AddMcpToolDeclarationSources` の後に呼ぶこと —— s2s の発行側は `Mcp:GrpcServices` が在るときにそちらが登録する
    // （実行の宛先も同じ構成なので、宛先が在れば発行側も在る）。宛先が 1 つも無い配備では実行器は常に fail-closed で拒否する。
    public static IServiceCollection AddMcpToolInvoker(this IServiceCollection services)
    {
        services.AddSingleton<GrpcToolInvoker>();
        services.AddSingleton<IToolInvoker>(sp => sp.GetRequiredService<GrpcToolInvoker>());
        return services;
    }
}
