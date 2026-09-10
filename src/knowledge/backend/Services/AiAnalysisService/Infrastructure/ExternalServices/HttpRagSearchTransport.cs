using System.Net.Http.Json;
using AiAnalysisService.Domain.Ports;
using Knowledge.Contracts.Dtos;

namespace AiAnalysisService.Infrastructure.ExternalServices;

// FR-03, FR-04, FR-05, FR-17, UC-01, UC-02, ADR-0034, ADR-0035, [[IADR-0263]] 残件 2,
// [[IADR-0418]], [[IADR-0425]] (#970 / #1255): RAG の検索呼び出しの **REST 輸送**。
//
// **並走中の正はこちらである**（[[IADR-0379]] 決定 5 / `ADR-0089` 決定 1）。
// `Services:RetrievalServiceGrpc` が構成されていなければ、`Program.cs` はこの実装を登録する。
//
// 🔴 **受信リクエストの `Authorization` を RetrievalService へそのまま伝播する（方式 A）。**
// 二段検索の段（グラフ近傍展開）はこのヘッダを GraphService まで運んでホップごと ABAC を
// 効かせる —— 伝播しないと、段を有効化しても RAG 経路の展開は常に 0 件だった（[[IADR-0263]] 残件 2）。
// **無ければ付けない**（縮退の判断とその警告は RetrievalService 側が一元で持つ。二重に持たない）。
//
// 🔴 **この転送は REST 輸送でしか落とせない**（[[IADR-0425]] 決定 5）。
// `POST /search` は `RequireAuthorization()` を持つ（[[IADR-0418]]）ので、落とすと 401 になる。
// **転送が消えるのは gRPC 輸送を選んだときだけ**であり、REST 実装の退役をもって
// 「経路 1 が解けた」と数える（`ADR-0089` 決定 1）。
public sealed class HttpRagSearchTransport(
    IHttpClientFactory httpFactory, IHttpContextAccessor? httpContextAccessor = null)
    : IRagSearchTransport
{
    /// <summary>名前つき HttpClient の名前（`Program.cs` の登録と一致させること）。</summary>
    public const string ClientName = "RetrievalService";

    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(
        RagSearchQuery query, CancellationToken ct)
    {
        var retrievalClient = httpFactory.CreateClient(ClientName);
        var auth = httpContextAccessor?.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            retrievalClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var searchResp = await retrievalClient.PostAsJsonAsync("/search",
                new SearchRequest(query.Query, query.TopK, null, query.EffectiveScope), ct);
            var searchResult = searchResp.IsSuccessStatusCode
                ? await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct)
                : new SearchResponse([], 0, 0);
            return searchResult?.Results ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 検索の不達は回答そのものを落とさない（現行どおり空へ縮退する）。
            return [];
        }
    }
}
