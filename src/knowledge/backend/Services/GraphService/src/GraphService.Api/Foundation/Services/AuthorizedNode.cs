using GraphService.Api.Foundation.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Services;

// FR-17, UC-10, ADR-0034 決定 1, IADR-0239 決定 2: **ホップごと ABAC の型ゲート（展開の入口）。**
//
// ADR-0034 決定 1 は「ホップごとに ABAC 判定を行う。終端でまとめてフィルタしない」を定める。
// 本リポジトリの認可判定 API（POST /authz/scope）が返すのは**フィルタ集合**であって資源ごとの
// 可否ではないため、共通作法「スコープ 1 回解決 → ローカル述語」を素朴に使うと
// **決定 1 が禁じる「探索してから濾す」形**になる。
//
// 🔴 **出力側のゲート（AuthorizedGraphView.Seal）だけでは足りない。** Seal は「未フィルタが外へ
// 出ない」ことは保証するが、「濾したのが**ホップごと**である」ことは保証しない —— 全ホップを
// 権限無視で展開してから Seal に渡す実装が書けてしまい、それがまさに禁じられた形である。
//
// **そこで展開の入口を型で塞ぐ。** 本型は private コンストラクタを持ち、構築経路は Authorize
// ただ 1 つである。探索の接続辺ロードは IReadOnlyList<AuthorizedNode> しか受け取らない
// （IGraphStore）。結果として:
//
//   - **非許可ノードから展開することが型として書けない。**
//   - したがって A→X→B（X が非許可）で B が浮上する「橋」は**構造的に成立しない**。
//   - 上限（ノード 200 / 辺 500）の計数も許可済み品目に対してしか行えないため、
//     件数が権限外の存在を漏らすことがない（ADR-0034 決定 4）。
//
// 構築経路の単一性は GraphTypeGateArchitectureTests がリフレクションで固定する。
public sealed class AuthorizedNode
{
    public GraphDocument Node { get; }

    public Guid DocumentId => Node.DocumentId;

    // 🔴 **private である。** ここを緩めると型ゲートが無効になる
    // （GraphTypeGateArchitectureTests が公開コンストラクタの不在を assert する）。
    private AuthorizedNode(GraphDocument node) => Node = node;

    // FR-17, ADR-0034 決定 1: **唯一の構築経路。** スコープを渡さずに AuthorizedNode は作れない。
    // 許可されなければ null を返す（例外にしない —— 探索は非許可ノードを「刈る」のであって
    // 失敗させるのではない）。
    public static AuthorizedNode? Authorize(GraphDocument node, AccessScopeResponse scope)
        => AbacNodeFilter.Matches(node, scope) ? new AuthorizedNode(node) : null;

    // UC-10: フロンティア用にまとめて判定する。**非許可ノードは黙って落ちる**
    // （件数も返さない —— ADR-0034 決定 2「見えない辺は完全に隠す」）。
    public static List<AuthorizedNode> AuthorizeAll(
        IEnumerable<GraphDocument> nodes, AccessScopeResponse scope)
    {
        var authorized = new List<AuthorizedNode>();
        foreach (var node in nodes)
        {
            var a = Authorize(node, scope);
            if (a is not null)
                authorized.Add(a);
        }
        return authorized;
    }
}
