using System.Diagnostics;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文 を RRF で統合 + ABAC フィルタ）。
//
// ADR-0068 決定 1: 端点はこの操作の処理であり 3 段目に置く。`MapGroup` とタグ付けは
// 集約の登録表（`SearchEndpoints`）に残る。
//
// 二段検索（ADR-0035）は同じ `IHybridSearchService` の着脱可能な段であって別の操作ではない
// ——本フォルダの `HybridSearchService` / `GraphExpandingSearchService` / `GraphRerank` /
// `GraphExpansionOptions` はいずれもこの操作だけが使う（ADR-0068 決定 2）。
internal static class SearchEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (SearchRequest req, IHybridSearchService search,
            ISearchAccessResolver access, HttpContext http, CancellationToken ct) =>
        {
            // 🔴 FR-05, NFR-09, ADR-0034 決定 1, [[IADR-0410]], [[IADR-0416]] (#1339):
            // **権限の根拠は自分で引く。** 本文の `Scope` は呼び出し元の主張であり、
            // ここでは**絞り込み**としてしか効かない（狭める方向にのみ作用する）。
            // 正直な呼び出し元にとって結果は変わらない —— 送ってくるのは同じ許可か、それを絞ったものである。
            // 🔴 **呼び出し元が「解決していない」ことは今も deny のままである**（[[IADR-0012]]）。
            // 権威で上書きすると、`Scope` を送らない呼び出し元が**今日より広く**見えることになる
            // —— 信頼を外す変更が、緩む向きの変更を連れてきてはならない。
            // **変えるのは「主張を信じるか」だけであり、「主張が要るか」ではない。**
            //
            // 🔴 **この短絡は REST 面だけのものである**（[[IADR-0426]] 決定 3）。gRPC 面は
            // 「主張が要る」形にしない —— 面が運ぶのは利用者文脈と絞り込みだけであり、
            // **絞り込みの不在は「絞らない」であって「解決していない」ではない**。
            if (req.Scope is not { GrantsAccess: true })
                return Results.Ok(new SearchResponse([], 0, 0));

            var effective = ScopeNarrowing.Apply(
                await access.ResolveAsync(http, ct), req.Scope);

            var sw = Stopwatch.StartNew();
            // 🔴 [[IADR-0426]] 決定 2: 利用者文脈は**入口が決めて**段まで引数で運ぶ。
            // 段が器（`IHttpContextAccessor`）から拾い直すと、east-west gRPC の入口で
            // 呼び出し元サービスの s2s 主体が利用者に化ける。
            var results = await ExecuteAsync(
                search, req, effective, SearchUserContext.FromRequest(http), ct);
            sw.Stop();
            return Results.Ok(new SearchResponse(results, results.Count, sw.ElapsedMilliseconds));
        }).WithName("Search").Produces<SearchResponse>();
    }

    // FR-03, FR-04, FR-05, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0412]] 決定 6,
    // [[IADR-0417]], [[IADR-0426]] 決定 3 (#1255):
    // 🔴 **REST と gRPC が通る唯一の検索。**
    //
    // 写すと、片方だけ deny の扱いが変わった状態が作れる —— 本リポジトリが繰り返し踏んでいる形である。
    // **交差の規則そのものは `ScopeNarrowing` が持つ**（入口ごとに主張の器が違うため、
    // 交差の呼び出しは入口に残る）。ここが持つのは
    // 「**許可が無ければ 1 件も返さない**」と「実効スコープで引く」の 2 つである。
    internal static async Task<List<SearchResultDto>> ExecuteAsync(
        IHybridSearchService search, SearchRequest request, AccessScope effective,
        SearchUserContext user, CancellationToken ct)
    {
        // deny-by-default: 交差の結果が「許可なし」なら何も返さない（[[IADR-0009]] の存在秘匿）。
        if (!effective.GrantsAccess)
            return [];

        return await search.SearchAsync(request with { Scope = effective }, user, ct);
    }
}
