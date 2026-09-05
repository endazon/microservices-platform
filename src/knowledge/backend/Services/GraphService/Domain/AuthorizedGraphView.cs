using GraphService.Domain;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Domain;

// FR-17, UC-10, ADR-0034 決定 2・4: 利用者へ返すグラフ表現（ノード）。
//
// SC-18, ADR-0054, IADR-0274 決定 2・3 (#917): `IsPrivateNote` は描き分け（組織文書＝円＋📄 /
// 個人資料＝角丸四角＋👤）のための 1 bit である。**漏洩の向きは検討済み** —— 値が付くのは
// ABAC 判定を通過して既に見えているノードだけであり、個人資料は所有者本人にしか見えない
// （権限外の文書はノードごと存在しない。ADR-0034 決定 2）。導出は Seal 時の
// GraphDocumentScope.IsPrivateNote（値が無い ⇒ 組織文書。ADR-0054 決定 5 が根拠）。
// 既定値つきで足す（既定値の無いメンバー追加は契約上の破壊的変更。IADR-0122 決定 2）。
public sealed record GraphNodeDto(Guid DocumentId, string Title, bool IsPrivateNote = false);

// FR-17, ADR-0033 決定 4: 利用者へ返すグラフ表現（辺）。
// **辺の型は識別子で返す**（表示名は型辞書の 1 回ロードで解決する。改名に追随するため）。
public sealed record GraphEdgeDto(
    Guid Id,
    Guid SourceDocumentId,
    Guid TargetDocumentId,
    Guid EdgeTypeId,
    string Provenance);

// FR-17, UC-10, ADR-0034 決定 1・2・4, IADR-0242 決定 2: **ホップごと ABAC の型ゲート（出力）。**
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
// **辺は両端点が許可されたときのみ可視である**（ADR-0034 決定 2 / IADR-0242 決定 6）。
// 端点の片方でも許可集合に無い辺は、件数にも匿名ノードにも一切現れない。
public sealed class GraphViewResponse
{
    public IReadOnlyList<GraphNodeDto> Nodes { get; }
    public IReadOnlyList<GraphEdgeDto> Edges { get; }

    // ADR-0034 決定 4: 表示上限で打ち切ったか。**上限の計数は述語通過後にのみ行う**ため、
    // このフラグが権限外文書の存在を漏らすことはない。
    public bool Truncated { get; }

    // FR-17, ADR-0049 決定 1・3 (#980): **許可済み**の総数。表示（200 / 500）を超えて数えた値。
    //
    // 🔴 **権限外は 1 件も含まない。** ADR-0049 決定 1 が「数えてよいのは ABAC 判定を通過した
    // 品目だけ」と明記しており、その条件下でのみ ADR-0034 の「中間結果に不許可文書が載らない」
    // という理由が保たれる。
    public int TotalNodes { get; }
    public int TotalEdges { get; }

    // ADR-0049 決定 3: 算出用の上限（ノード 2,000 / 辺 5,000）に達したか。
    // **立っていれば総数は「以上」の意味である**（例: 「2,000 件以上」）。
    //
    // ⚠️ **母集合が算出用上限で打ち切られている場合、`updated` / `degree` の並びは厳密な
    // 上位 200 件ではない**（ADR-0049 決定 4 がこの限界を認めている）。**本フラグが立っている
    // ことで読み取れる** —— 黙って正確なふりをしない。
    public bool TotalIsLowerBound { get; }

    // 🔴 **private である。** ここを緩めると出力ゲートが無効になる
    // （GraphTypeGateArchitectureTests が公開コンストラクタの不在を assert する）。
    private GraphViewResponse(
        IReadOnlyList<GraphNodeDto> nodes,
        IReadOnlyList<GraphEdgeDto> edges,
        bool truncated,
        int totalNodes,
        int totalEdges,
        bool totalIsLowerBound)
    {
        Nodes = nodes;
        Edges = edges;
        Truncated = truncated;
        TotalNodes = totalNodes;
        TotalEdges = totalEdges;
        TotalIsLowerBound = totalIsLowerBound;
    }

    // FR-17, ADR-0034 決定 1・2: **唯一の構築経路。**
    internal static GraphViewResponse Seal(UnfilteredSubgraph subgraph, AccessScopeResponse scope)
    {
        // FR-05: deny-by-default。許可ポリシーが無ければ何も返さない。
        if (!scope.Granted)
            return new GraphViewResponse([], [], subgraph.Truncated, 0, 0, false);

        // 多層防御: 入口のゲートを通っていれば恒等だが、迂回経路があってもここで濾す。
        var visible = new List<GraphDocument>();
        var visibleIds = new HashSet<Guid>();
        foreach (var node in subgraph.Nodes)
        {
            if (AuthorizedNode.Authorize(node, scope) is null)
                continue;
            // 🔴 FR-19, ADR-0061 決定 3 / [[IADR-0395]] 決定 6 (#1184): **多層防御。**
            // 生産側（`GraphDocumentSyncConsumer`）がノードを作らない・消すのが一次の守りで、
            // ここは**迂回経路と取りこぼしに対する二次の守り**である（`AuthorizedNode` の
            // 恒等ゲートと同じ位置づけ）。判定は同じ `DocumentExposure.IsGraphAllowed`。
            if (!DocumentExposure.IsGraphAllowed(node.Attributes))
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
            .Select(n => new GraphNodeDto(n.DocumentId, n.Title, GraphDocumentScope.IsPrivateNote(n.Attributes)))
            .ToList();

        return new GraphViewResponse(nodes, edges, subgraph.Truncated,
            subgraph.TotalNodes, subgraph.TotalEdges, subgraph.TotalIsLowerBound);
    }
}
