using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Domain;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034: グラフのノードに ABAC 許可スコープを適用する純粋ロジック。
//
// **評価意味論は WikiService の AbacPageFilter・検索側の InMemoryVectorStore.MatchesFilters・
// AuthorizationService の AbacEvaluator と一致させる**（一致は AbacNodeFilterTests が固定する）:
//   - Granted=false（マッチするポリシー無し）→ deny-by-default。何も可視でない。
//   - フィルタ間は AND、値集合内は OR。
//   - **属性キーを持たないノードは不一致**（欠落は安全側に倒す）。
//   - AllowedFilters が空 かつ Granted=true → 条件無しで全件可。
//
// FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253 決定 1（段 3・本サービスの分岐対応 / #989）:
//   Branches が 1 件以上あれば **いずれかの分岐のフィルタをすべて満たすノードが可視**
//   （分岐内 AND・分岐間 OR）。1 分岐 = マッチした 1 ポリシーの文書条件であり、
//   計画の read 規則「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の選言を写す。
//   分岐のフィルタが空 = 文書条件の無いポリシー =（そのポリシーの範囲で）全件許可。
//   Branches が空/null なら従来どおり AllowedFilters で評価する（後方互換。未移行の応答）。
//   ${current_user} は認可サービスが分岐の中で束縛済みであり、ここでは解釈しない
//   （素の文字列比較のみ。IADR-0253 決定 3——述語が解釈すると認可の判断が 2 箇所へ散る）。
//
//   🔴 **キー単位 union へ畳まない**（IADR-0253 決定 2 の反例）——
//   A={confidentiality:internal, department:hr} と B={confidentiality:public, department:sales}
//   を union すると、**どちらのポリシー単独も許可しない混成 (internal, sales) を許す**。
//
// 🔴 **本クラスは単独では ADR-0034 決定 1 を満たさない。** 述語をどこで適用するかが決定 1 の
// 論点であり、「探索してから濾す」形を防ぐのは AuthorizedNode の型ゲートである（IADR-0242 決定 2）。
// 述語を直接呼ぶのではなく AuthorizedNode.Authorize を通すこと。
public static class AbacNodeFilter
{
    // 1 ノードが許可スコープに合致するか。
    public static bool Matches(GraphDocument node, AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければいかなるノードも不可視。
        if (!scope.Granted)
            return false;

        // FR-19, IADR-0253: 分岐があれば選言で評価する（分岐間 OR・分岐内 AND）。
        //
        // 🔴 FR-19, ADR-0061 決定 5・6, [[IADR-0396]] 決定 7 (#1184):
        // **個人資料を許可してよいのは裁量（`owner` / `shared_with`）の分岐だけ**である。
        // グラフ露出 ON の個人資料がノードとして在り得るようになったため、検索側と**同じ述語**で
        // 閉じる（`PrivateNoteVisibility`。3 つの消費面で 1 か所）。
        if (scope.Branches is { Count: > 0 })
            return scope.Branches.Any(b =>
                MatchesAll(node, b.Filters)
                && PrivateNoteVisibility.BranchMayGrant(node.Attributes, b.Filters));

        // 条件無しの許可（全件可）。
        if (scope.AllowedFilters is not { Count: > 0 })
            return true;

        // フィルタ間 AND、値集合内 OR。属性欠落は不一致。
        return MatchesAll(node, scope.AllowedFilters);
    }

    // フィルタ間 AND、値集合内 OR。属性キーを持たないノードは不一致（欠落は安全側に倒す）。
    private static bool MatchesAll(GraphDocument node, List<AttributeFilter> filters) =>
        filters.All(f =>
            node.Attributes.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
}
