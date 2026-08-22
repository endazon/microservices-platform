using GraphService.Api.Foundation.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Services;

// FR-17, FR-05, UC-10, ADR-0004, ADR-0034: グラフのノードに ABAC 許可スコープを適用する純粋ロジック。
//
// **評価意味論は WikiService の AbacPageFilter・検索側の InMemoryVectorStore.MatchesFilters・
// AuthorizationService の AbacEvaluator と一致させる**（一致は AbacNodeFilterTests が固定する）:
//   - Granted=false（マッチするポリシー無し）→ deny-by-default。何も可視でない。
//   - フィルタ間は AND、値集合内は OR。
//   - **属性キーを持たないノードは不一致**（欠落は安全側に倒す）。
//   - AllowedFilters が空 かつ Granted=true → 条件無しで全件可。
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

        // 条件無しの許可（全件可）。
        if (scope.AllowedFilters is not { Count: > 0 })
            return true;

        // フィルタ間 AND、値集合内 OR。属性欠落は不一致。
        return scope.AllowedFilters.All(f =>
            node.Attributes.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
    }
}
