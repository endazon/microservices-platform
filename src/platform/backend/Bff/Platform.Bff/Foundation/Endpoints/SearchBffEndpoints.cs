using Platform.Shared.Infrastructure.Foundation.Authz;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;

namespace Platform.Bff.Foundation.Endpoints;

// FR-03, FR-05, UC-01, SC-01: BFF 検索集約エンドポイント。
// 横断検索は「ABAC スコープ解決（AuthorizationService）→ ハイブリッド検索（RetrievalService）」を集約する。
// スコープはサーバ側で JWT から解決し、クライアント指定の Scope は信頼しない（権限昇格の防止）。
// deny-by-default: 許可ポリシーが無ければ空を返す（権限外の存在を示さない・IADR-0009 と整合）。
public static class SearchBffEndpoints
{
    // FR-03: 既定・上限の TopK（過大要求を抑止）。
    private const int DefaultTopK = 10;
    private const int MaxTopK = 50;

    public static IEndpointRouteBuilder MapSearchBffEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/bff/search").WithTags("Search BFF");

        // FR-03, UC-01: 横断検索。要求は query（＋任意 topK）。Scope はクライアントから受け取らずサーバで解決する。
        g.MapPost("/", async (
            SearchRequest req,
            IHttpClientFactory httpFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.Ok(new SearchResponse([], 0, 0));

            var topK = Math.Clamp(req.TopK <= 0 ? DefaultTopK : req.TopK, 1, MaxTopK);

            // FR-05: 利用者の ABAC 許可スコープをサーバ側で解決する（deny-by-default。クライアント指定
            // Scope は信頼しない）。許可ポリシーが無い／認可サービス不調は空応答へ縮退する（存在秘匿）。
            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
            if (scope is null)
                return Results.Ok(new SearchResponse([], 0, 0));

            // FR-03: 解決済みスコープでハイブリッド検索を実行する（クライアント指定 Scope は使わない）。
            var retrievalClient = httpFactory.CreateClient("RetrievalService");
            try
            {
                var searchResp = await retrievalClient.PostAsJsonAsync("/search",
                    new SearchRequest(req.Query, topK, req.AttributeFilters, scope), ct);
                if (!searchResp.IsSuccessStatusCode)
                    return Results.StatusCode((int)searchResp.StatusCode);

                var result = await searchResp.Content.ReadFromJsonAsync<SearchResponse>(ct);
                return Results.Ok(result ?? new SearchResponse([], 0, 0));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Ok(new SearchResponse([], 0, 0));
            }
        }).WithName("BffSearch").Produces<SearchResponse>();

        return app;
    }
}
