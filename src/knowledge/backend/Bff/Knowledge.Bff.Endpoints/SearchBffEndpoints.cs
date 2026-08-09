using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Platform.Shared.Infrastructure.Foundation.Authz;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

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
                // #531: 検索モードは利用者の指定をそのまま透過する（Scope と違い信頼性の問題が無い——
                // モードは絞り込みの種類であって権限ではない）。未知値の縮退は RetrievalService 側が行う。
                var searchResp = await retrievalClient.PostAsJsonAsync("/search",
                    // FR-03, SC-02（#531 / #532）: 検索モードと並び順は**利用者の指定をそのまま後段へ渡す**。
                    // 縮退（未知値 → 既定）は RetrievalService が一箇所で行う（BFF で二重に正規化しない）。
                    new SearchRequest(req.Query, topK, req.AttributeFilters, scope, req.Mode, req.SortBy), ct);
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

        // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043・裁定 Q2）。
        // SC-01 / SC-08 の対象範囲フィルタが「**権限内のタグ／部門／プロジェクトのみ選択可**」を
        // 満たすための候補一覧である。**一般利用者が呼べる**（従前は管理者限定の辞書しか無かった）。
        //
        // **スコープはサーバ側で解決し、クライアント指定は信頼しない**（検索と同じ。権限昇格の防止）。
        // **解決できないときは空配列**（[[IADR-0151]] 決定 5）——404 にも 403 にもしない。
        // **候補が無いことと権限が無いことを利用者へ区別させない。**
        var values = app.MapGroup("/bff/attribute-values").WithTags("Search BFF");

        values.MapPost("/", async (
            AttributeValuesRequest req,
            IHttpClientFactory httpFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Key))
                return Results.Ok(new AttributeValuesResponse([]));

            var scope = await BffScopeResolver.ResolveAsync(httpFactory, http, ct);
            if (scope is null)
                return Results.Ok(new AttributeValuesResponse([]));

            var retrievalClient = httpFactory.CreateClient("RetrievalService");
            try
            {
                // **クライアントが送ってきた Scope は使わない**（解決済みで置き換える）。
                var resp = await retrievalClient.PostAsJsonAsync("/search/attribute-values",
                    new AttributeValuesRequest(req.Key, scope), ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.StatusCode((int)resp.StatusCode);

                var result = await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(ct);
                return Results.Ok(result ?? new AttributeValuesResponse([]));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // **後段へ到達できないときだけ**空配列へ縮退する（検索と同じ扱い。存在秘匿を崩さない）。
                // **後段が返した非 2xx は上で透過済み**である —— 障害を 200 空応答で隠さない。
                return Results.Ok(new AttributeValuesResponse([]));
            }
        }).WithName("BffAttributeValues").Produces<AttributeValuesResponse>();

        return app;
    }
}
