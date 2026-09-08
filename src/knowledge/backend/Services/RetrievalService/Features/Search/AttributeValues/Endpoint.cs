using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.AttributeValues;

// FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043・裁定 Q2）。
//
// **辞書を丸ごと返さない**（決定 1）——返すのは「**到達できる文書に実際に付与された値**」だけ。
// **件数を返さない**（決定 2）——`AttributeValuesResponse` は値の配列しか持たない。
// **検索と同じ ABAC フィルタを使う**（[[IADR-0151]] 決定 1）——別経路で数えると
// 「検索には出るが候補に無い値」が生まれる。
//
// **スコープが解決できない（deny-by-default）ときは空配列**（[[IADR-0151]] 決定 5）。
// 404 にも 403 にもしない——**候補が無いことと権限が無いことを区別させない**。
internal static class AttributeValuesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/attribute-values", async (
            AttributeValuesRequest req, IVectorStore store,
            ISearchAccessResolver access, HttpContext http, CancellationToken ct) =>
        {
            // deny-by-default: 呼び出し元が解決できなかった（null）／許可なしのスコープでは何も返さない。
            // 🔴 **これは従来どおりである**（[[IADR-0151]] 決定 5）——
            // 本 PR が変えるのは「主張を信じるか」だけであり、「主張が要るか」ではない。
            if (req.Scope is not { GrantsAccess: true } || string.IsNullOrWhiteSpace(req.Key))
                return Results.Ok(new AttributeValuesResponse([]));

            // 🔴 FR-05, NFR-09, [[IADR-0410]], [[IADR-0416]] (#1339): **権限の根拠は自分で引く。**
            // 本文の `Scope` は呼び出し元の主張であり、**絞り込みとしてしか効かない**。
            // deny-by-default: 自分で引いた許可が無ければ何も返さない（存在秘匿は現行のまま）。
            var narrowed = ScopeNarrowing.Apply(
                await access.ResolveAsync(http, ct), req.Scope);
            if (narrowed is not { GrantsAccess: true } scope)
                return Results.Ok(new AttributeValuesResponse([]));

            return Results.Ok(new AttributeValuesResponse(
                await ListAsync(store, req.Key, scope, ct)));
        }).WithName("AttributeValues").Produces<AttributeValuesResponse>();
    }

    // FR-04, FR-05, NFR-16, [[IADR-0151]] 決定 1, [[IADR-0253]] 決定 2, [[IADR-0417]] (#1255):
    // 🔴 **REST と gRPC が通る唯一の問い合わせ。**
    //
    // 写すと、片方だけ分岐の扱いが変わった状態が作れる —— 本リポジトリが繰り返し踏んでいる形である
    // （直近では [[IADR-0412]] 決定 6 が同じ理由で 1 つに寄せた）。
    //
    // 🔴 **分岐があるときは旧算出値 `scope.Filters`（キー単位 union）を連言に残さない** ——
    // union は分岐の和の上位集合ではない（[[IADR-0253]] 決定 2 の非包含）ため、
    // 余分に AND すると分岐単独で到達できる文書の値が候補から落ちる
    // （`HybridSearchService.BuildFilters` と同形。波 2 監査の是正）。
    //
    // 🔴 **検索と同じ制約を渡す**（別経路で絞ると「検索には出るが候補に無い値」が生まれる）。
    internal static Task<List<string>> ListAsync(
        IVectorStore store, string key, AccessScope scope, CancellationToken ct) =>
        store.ListAttributeValuesAsync(
            AttributeValueKeys.ToPayloadKey(key),
            scope.Branches is { Count: > 0 }
                ? new ScopeFilter(
                    [],
                    [.. scope.Branches.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)])
                : new ScopeFilter(scope.Filters),
            ct);
}
