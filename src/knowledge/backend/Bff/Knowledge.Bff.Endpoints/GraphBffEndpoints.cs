using Knowledge.Contracts.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;
using System.Net.Http.Json;

namespace Knowledge.Bff.Endpoints;

// FR-17, UC-10, ADR-0034, ADR-0043: グラフ読み取りを BFF から公開する（#916a）。
//
// 🔴 **権限伝播は「Authorization ヘッダの伝播」を採る。**
//
// 本リポジトリの BFF には権限伝播が 2 方式ある。
//   A) 利用者の JWT を後段へ伝播する（AnalysisBffEndpoints / AuthzBffEndpoints）
//   B) BFF が解決した AccessScope を本文へ載せる（SearchBffEndpoints → RetrievalService）
//
// **判断の軸は「後段が自分で ABAC を解決する型かどうか」である。**
// GraphService は自分で JWT から解決する（GraphAccessResolver。#908）ため A を採る。
//
// 🔴 **B を採ってはならない。** 本文で渡された scope を GraphService が信頼する形にすると、
// **その経路へ到達できる誰もが任意の scope を主張できる** —— ホップごと ABAC の型ゲート
// （IADR-0242 決定 2）が入力の時点で無意味になる。「RetrievalService が B だから揃える」は
// 理由にならない。あちらの設計判断を、自分で解決する型の後段へ横展開しない。
//
// ⚠️ **ヘッダを伝播し忘れると「全部 404」で静かに壊れる。** GraphService は利用者を
// anonymous として解決し Granted=false へ縮退するためである。動くように見える壊れ方では
// ないが「グラフには何も無い」と読めるので、陽性対照つきのテストで固定する（#916a 仕様書）。
public static class GraphBffEndpoints
{
    public static IEndpointRouteBuilder MapGraphBffEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09: 認証のみ（ロール不問）。可視性は GraphService の ABAC が決める。
        var g = app.MapGroup("/bff/graph").WithTags("Graph BFF").RequireAuthorization();

        // FR-17, UC-10: 起点ノード 1 件。
        g.MapGet("/{documentId:guid}", (Guid documentId, IHttpClientFactory httpFactory,
                HttpContext http, CancellationToken ct)
            => ProxyAsync<GraphViewDto>(httpFactory, http, $"/graph/{documentId}", ct))
            .WithName("BffGraphNode")
            .Produces<GraphViewDto>()
            .Produces(StatusCodes.Status404NotFound);

        // FR-17, UC-10, ADR-0034 決定 3: 近傍探索。
        // **hops はそのまま後段へ渡す。** 上限超過の拒否（400）は GraphService が一箇所で行い、
        // BFF では正規化も握り潰しもしない（検索モードを後段へ透過する SearchBff と同じ作法）。
        g.MapGet("/{documentId:guid}/neighbors", (Guid documentId, int? hops,
                IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
            => ProxyAsync<GraphViewDto>(httpFactory, http,
                $"/graph/{documentId}/neighbors" + (hops is null ? "" : $"?hops={hops}"), ct))
            .WithName("BffGraphNeighbors")
            .Produces<GraphViewDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        // FR-17, SC-18 (#962): 辺の型カタログ。**辺の型名を解決するのに要る** ——
        // グラフ応答が返すのは `EdgeTypeId` だけなので、これが無いと辺の描き分けも型フィルタも描けない。
        //
        // 🔴 **後段は `/graph/edge-types/catalog`（認証のみ・使用件数なし）であって、
        // `/graph/edge-types`（admin / operator 限定・使用件数つき）ではない。**
        // 後者へ向けると (1) 一般利用者が 403 になり (2) ABAC で絞られていない集計値が漏れる。
        //
        // ルートの衝突は起きない —— 上の 2 本は `{documentId:guid}` に制約されており、
        // `edge-types` は GUID として解釈されない。
        g.MapGet("/edge-types", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct)
            => ProxyAsync<List<EdgeTypeCatalogItemDto>>(httpFactory, http, "/graph/edge-types/catalog", ct))
            .WithName("BffGraphEdgeTypes")
            .Produces<List<EdgeTypeCatalogItemDto>>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    // GraphService へ中継する。
    //
    // 🔴 **状態コードはそのまま返す。** とくに 404 を別の値へ置き換えない ——
    // GraphService は「権限外・複製未到達・不存在」をすべて 404 に倒して存在を秘匿しており
    // （ADR-0034 決定 2）、BFF がここで 403 や 200 へ変換すると**その秘匿が BFF 層で破れる**。
    private static async Task<IResult> ProxyAsync<T>(
        IHttpClientFactory httpFactory, HttpContext http, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient("GraphService");

        // FR-05: ABAC 権限解決のため、利用者の資格情報を後段へ引き継ぐ（権限外文書を出さない）。
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", auth);

        try
        {
            var resp = await client.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode((int)resp.StatusCode);

            var view = await resp.Content.ReadFromJsonAsync<T>(ct);
            return view is null
                ? Results.StatusCode(StatusCodes.Status502BadGateway)
                : Results.Ok(view);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 後段へ到達できない。**空応答へ縮退しない** —— グラフが「空」と「引けない」は
            // 利用者にとって別の意味であり、空を返すと「関係が無い」と読めてしまう。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
