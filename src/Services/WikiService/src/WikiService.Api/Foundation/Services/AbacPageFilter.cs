using WikiService.Api.Foundation.Ports;
using KnowledgePlatform.Shared.Contracts.Dtos;
using WikiService.Api.Foundation.Domain;

namespace WikiService.Api.Foundation.Services;

// FR-13, FR-05, UC-07, ADR-0011, ADR-0004: Wiki ページに ABAC 許可スコープを適用する純粋ロジック。
//
// 不変条件: ABAC は本システムがソースオブトゥルース（ADR-0011）。Wiki 閲覧（一覧・本文）でも
//   「権限の無い文書は一切現れない」（FR-13 受け入れ基準②・UC-07 例外フロー）を保証する。
//
// 評価意味論（検索側 InMemoryVectorStore.MatchesFilters / AbacEvaluator と一致）:
//   - Granted=false（マッチするポリシー無し）→ deny-by-default。何も可視でない。
//   - フィルタ間は AND、値集合内は OR。
//   - 属性キーを持たない文書は不一致（欠落は安全側に倒す）。
//   - AllowedFilters が空 かつ Granted=true → 条件無しで全件可。
public static class AbacPageFilter
{
    // 1 ページが許可スコープに合致するか。
    public static bool Matches(WikiPage page, AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければいかなるページも不可視。
        if (!scope.Granted)
            return false;

        // 条件無しの許可（全件可）。
        if (scope.AllowedFilters is not { Count: > 0 })
            return true;

        // フィルタ間 AND、値集合内 OR。属性欠落は不一致。
        return scope.AllowedFilters.All(f =>
            page.Attributes.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
    }

    // 可視ページのみを返す。
    public static IEnumerable<WikiPage> Filter(IEnumerable<WikiPage> pages, AccessScopeResponse scope)
    {
        if (!scope.Granted)
            return [];
        return pages.Where(p => Matches(p, scope));
    }
}
