using AiAnalysisService.Domain.Ports;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace AiAnalysisService.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, FR-07, FR-17, NFR-09, NFR-16, UC-01, UC-02, UC-10, SC-01, SC-08,
// ADR-0004, ADR-0029, ADR-0034 決定 1, ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1,
// ADR-0087 決定 2, ADR-0089 決定 1, [[IADR-0379]] 決定 4・5, [[IADR-0400]], [[IADR-0410]],
// [[IADR-0415]], [[IADR-0416]], [[IADR-0425]] (#1255):
// RAG の検索呼び出しの **east-west gRPC 輸送**。
//
// **並走中の正は REST である。** 本実装は `Services:RetrievalServiceGrpc` が構成されたときだけ
// 登録され（`AddRetrievalSearchGrpcClient`）、無ければ `HttpRagSearchTransport` のままである。
// 戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **利用者の JWT はメタデータへ載せない**（`ADR-0086` 決定 1 / [[IADR-0379]] 決定 4）。
// 載るのは**本サービス自身の s2s トークン**だけであり、利用者の文脈（`user_id` / 属性 / `action`）は
// **本文で**運ぶ —— 通すと呼び出し先が「利用者が直接呼んだ」と「サービスが利用者のために呼んだ」を
// 区別できず、利用者ロールがサービス間の面へ漏れる（confused deputy）。
// **これが `ADR-0086` 実装側残作業 2「経路 1 の利用者トークン転送を落とす」の実体である。**
//
// 🔴 **判定の位置は動かない**（`ADR-0034` 決定 1）。RetrievalService は受け取った利用者文脈で
// **自分の**スコープを解決してから検索する。**解決済み scope を本文で渡す方式 B は採らない** ——
// 採ると受け口に「本文で渡された scope を信じる」口が開き、そこへ到達できる誰もが
// 任意の scope を主張できる（#1339 で塞いだ穴を輸送で開け直すことになる）。
//
// 🔴 **絞り込みは交差前の値を送る。** 呼び出し元が交差した実効スコープ（`EffectiveScope`）は
// 送らない —— 送っても受け口は絞り込みとしてしか使わないが、**分岐を平坦化して運ぶ形になり**、
// [[IADR-0253]] 決定 2 が退けた「キー単位 union」で分岐の混成を許すことになる。
public sealed class GrpcRagSearchTransport(
    Pb.DocumentSearch.DocumentSearchClient client,
    ILogger<GrpcRagSearchTransport> logger) : IRagSearchTransport
{
    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(
        RagSearchQuery query, CancellationToken ct)
    {
        var request = new Pb.SearchRequest
        {
            Query = query.Query,
            TopK = query.TopK,
            User = ToUserContext(query),
        };

        foreach (var (key, values) in query.NarrowTo ?? new Dictionary<string, List<string>>())
            request.NarrowTo[key] = new Pb.NarrowTo { Values = { values } };

        try
        {
            var resp = await client.SearchAsync(request, cancellationToken: ct);
            return [.. resp.Results.Select(ToDto).OfType<SearchResultDto>()];
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            // 🔴 REST の「非 2xx」「不達」と**同じ枝**へ落とす（[[IADR-0425]] 決定 4）。
            // gRPC には「非 2xx」に相当する概念が無く、到達失敗も応答の失敗も等しく
            // `RpcException` になる。s2s トークン取得失敗（`InvalidOperationException`）も同じ枝である
            // —— **配線漏れが「該当が無い」に見えるのは避けられないので、必ず警告を出す。**
            logger.LogWarning(ex,
                "Retrieval search over gRPC failed; the answer degrades to no citations");
            return [];
        }
    }

    // 🔴 **載せるのは判定の入力であって判定結果ではない**（計画 `ADR-0086` 決定 1）。
    // 属性の抽出点は呼び出し元（端点）が既に通しており（`BffScopeResolver.ExtractUserAttributes`。
    // [[IADR-0411]]）、ここでキーを列挙しない。
    private static Pb.UserContext ToUserContext(RagSearchQuery query)
    {
        var user = new Pb.UserContext
        {
            UserId = query.UserId,
            // 🔴 **`action` は既定へ丸めない。** 読み取り経路も `read` を明示して送る
            // （[[IADR-0272]] 決定 4 / [[IADR-0401]] と同じ作法）。
            Action = "read",
        };
        foreach (var (key, value) in query.UserAttributes)
            user.UserAttributes[key] = value;
        return user;
    }

    // 🔴 **識別子が読めない結果は捨てるが、黙って捨てない。**
    // 起こり得るのは呼び出し先の欠陥だけであり、静かに件数が減ると
    // 「検索に出なかった」と区別できなくなる。
    private SearchResultDto? ToDto(Pb.SearchResult result)
    {
        if (!Guid.TryParse(result.ChunkId, out var chunkId)
            || !Guid.TryParse(result.DocumentId, out var documentId))
        {
            logger.LogWarning(
                "Retrieval search returned a result with an unreadable identifier "
                + "(chunkId={ChunkId}); it is dropped from the RAG context", result.ChunkId);
            return null;
        }

        return new SearchResultDto(
            chunkId,
            documentId,
            result.DocumentTitle,
            result.Text,
            result.Score,
            // 🔴 未設定（null）と空文字は別物である（presence で運ばれている）。
            result.HasMarkdownUri ? result.MarkdownUri : null,
            new Dictionary<string, string>(result.Attributes),
            [.. result.Tags],
            // 🔴 未設定は「まだ索引に無い」である（既定値で埋めない）。
            result.UpdatedAt?.ToDateTimeOffset(),
            // 🔴 DTO の既定は `true`・proto3 の既定は `false` で向きが逆である。
            // **明示的に写す**（呼び出し先も明示的に書いている）。
            result.HasBody);
    }

    // 🔴 縮退させるのは**輸送と s2s の失敗だけ**である（`GrpcLlmCompletionTransport` と同じ規則）。
    // `OperationCanceledException`（利用者による中断）とそれ以外の予期しない例外は**そのまま上げる**
    // —— 何でも縮退させると実装の誤りが「該当が無い」に化けて見えなくなる。
    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex is RpcException or InvalidOperationException && !ct.IsCancellationRequested;
}

// FR-03, FR-04, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0425]] (#1255):
// RetrievalService 宛の生成クライアントの登録。`AddGraphNeighborsGrpcClient` と同型。
public static class RetrievalSearchGrpcClientExtensions
{
    /// <summary>`Services:RetrievalServiceGrpc`（h2c のアドレス。例: http://retrieval-service:8081）。</summary>
    public const string AddressKey = "Services:RetrievalServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "RetrievalServiceGrpc";

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddRetrievalSearchGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。本サービスは既に認可サービス宛のチャネルを
        // **キー無し**で持ち（`AuthzScopeGrpcClient.AddChannel` の `TryAddSingleton`）、
        // LlmGateway 宛をキー `LlmGatewayGrpc` で持つ —— 3 本目をキー無しで足すと、
        // 検索が**認可サービスへ繋がる**（あるいはその逆）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.DocumentSearch.DocumentSearchClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
