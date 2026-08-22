using GraphService.Api.Foundation.Domain;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Services;

// FR-17, UC-10, ADR-0034 決定 2・4: 利用者へ返すグラフ表現（ノード）。
public sealed record GraphNodeDto(Guid DocumentId, string Title);

// FR-17, ADR-0033 決定 4: 利用者へ返すグラフ表現（辺）。
// **辺の型は識別子で返す**（表示名は型辞書の 1 回ロードで解決する。改名に追随するため）。
public sealed record GraphEdgeDto(
    Guid Id,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    Guid EdgeTypeId,
    string Provenance);

// FR-17, UC-10, ADR-0034 決定 1・2・4, IADR-0241 決定 2: **ホップごと ABAC の型ゲート（出力）。**
//
// 応答 DTO は private コンストラクタを持ち、構築経路は AuthorizedGraphView.Seal ただ 1 つである。
// Seal は未フィルタの部分グラフと**許可スコープの両方**を要求するため、
// **未フィルタの結果を直列化する経路がコンパイル時に存在しない。**
//
// 🔴 **Seal は述語を再適用する。** AuthorizedNode（展開の入口のゲート）を通っていれば結果は
// 変わらないが、多層防御として意味がある —— 入口のゲートを迂回した経路があっても、
// **出口で必ず濾される**。2 つのゲートは別々の性質を担保している:
//   - AuthorizedNode  → 濾したのが**ホップごと**であること（橋を作らせない）
//   - Seal            → 未フィルタが**外へ出ない**こと
//
// **辺は両端点が許可されたときのみ可視である**（ADR-0034 決定 2 / IADR-0241 決定 6）。
// 端点の片方でも許可集合に無い辺は、件数にも匿名ノードにも一切現れない。
public sealed class GraphViewResponse
{
    public IReadOnlyList<GraphNodeDto> Nodes { get; }
    public IReadOnlyList<GraphEdgeDto> Edges { get; }

    // ADR-0034 決定 4: 表示上限で打ち切ったか。**上限の計数は述語通過後にのみ行う**ため、
    // このフラグが権限外文書の存在を漏らすことはない。
    public bool Truncated { get; }

    // 🔴 **private である。** ここを緩めると出力ゲートが無効になる
    // （GraphTypeGateArchitectureTests が公開コンストラクタの不在を assert する）。
    private GraphViewResponse(
        IReadOnlyList<GraphNodeDto> nodes,
        IReadOnlyList<GraphEdgeDto> edges,
        bool truncated)
    {
        Nodes = nodes;
        Edges = edges;
        Truncated = truncated;
    }

    // FR-17, ADR-0034 決定 1・2: **唯一の構築経路。**
    internal static GraphViewResponse Seal(UnfilteredSubgraph subgraph, AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ何も返さない。
        if (!scope.Granted)
            return new GraphViewResponse([], [], subgraph.Truncated);

        // 多層防御: 入口のゲートを通っていれば恒等だが、迂回経路があってもここで濾す。
        var visible = new List<GraphDocument>();
        var visibleIds = new HashSet<Guid>();
        foreach (var node in subgraph.Nodes)
        {
            if (AuthorizedNode.Authorize(node, scope) is null)
                continue;
            visible.Add(node);
            visibleIds.Add(node.DocumentId);
        }

        // ADR-0034 決定 2: **両端点が許可された辺だけを返す。**
        // 片方でも欠ければ、その辺は存在しなかったものとして扱う（件数にも出さない）。
        var edges = subgraph.Edges
            .Where(e => visibleIds.Contains(e.SourceDocumentId)
                     && visibleIds.Contains(e.TargetDocumentId))
            .Select(e => new GraphEdgeDto(
                e.Id, e.SourceDocumentId, e.TargetDocumentId, e.EdgeTypeId, e.Provenance))
            .ToList();

        var nodes = visible
            .Select(n => new GraphNodeDto(n.DocumentId, n.Title))
            .ToList();

        return new GraphViewResponse(nodes, edges, subgraph.Truncated);
    }
}
