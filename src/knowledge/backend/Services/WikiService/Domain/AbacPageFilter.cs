using WikiService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using WikiService.Domain;

namespace WikiService.Domain;

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
//
// FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 決定 1（段 3・本サービスの分岐対応）:
//   Branches が 1 件以上あれば **いずれかの分岐のフィルタをすべて満たすページが可視**
//   （分岐内 AND・分岐間 OR）。1 分岐 = マッチした 1 ポリシーの文書条件であり、
//   計画の read 規則「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の選言を写す。
//   分岐のフィルタが空 = 文書条件の無いポリシー =（そのポリシーの範囲で）全件許可。
//   Branches が空/null なら従来どおり AllowedFilters で評価する（後方互換。未移行の応答）。
//   ${current_user} は認可サービスが分岐の中で束縛済みであり、ここでは解釈しない
//   （素の文字列比較のみ。IADR-0253 決定 3——述語が解釈すると認可の判断が 2 箇所へ散る）。
public static class AbacPageFilter
{
    // 1 ページが許可スコープに合致するか。
    public static bool Matches(WikiPage page, AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければいかなるページも不可視。
        if (!scope.Granted)
            return false;

        // FR-19, IADR-0253: 分岐があれば選言で評価する（分岐間 OR・分岐内 AND）。
        if (scope.Branches is { Count: > 0 })
            return scope.Branches.Any(b => MatchesAll(page, b.Filters));

        // 条件無しの許可（全件可）。
        if (scope.AllowedFilters is not { Count: > 0 })
            return true;

        // フィルタ間 AND、値集合内 OR。属性欠落は不一致。
        return MatchesAll(page, scope.AllowedFilters);
    }

    // フィルタ間 AND、値集合内 OR。属性キーを持たないページは不一致（欠落は安全側に倒す）。
    private static bool MatchesAll(WikiPage page, List<AttributeFilter> filters) =>
        filters.All(f =>
            page.Attributes.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));

    // 可視ページのみを返す。
    public static IEnumerable<WikiPage> Filter(IEnumerable<WikiPage> pages, AccessScopeResponse scope)
    {
        if (!scope.Granted)
            return [];
        return pages.Where(p => Matches(p, scope));
    }
}
