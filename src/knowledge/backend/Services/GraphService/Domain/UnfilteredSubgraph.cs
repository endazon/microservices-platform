using GraphService.Domain;

namespace GraphService.Domain;

// FR-17, UC-10, ADR-0034 決定 1, IADR-0242 決定 2: 探索・永続化層が返す**未フィルタの**部分グラフ。
//
// 🔴 **internal である。** 本型は API 応答になれない —— 応答 DTO（GraphViewResponse）は
// AuthorizedGraphView.Seal からしか構築できず、Seal は本型とスコープの両方を要求する。
// つまり**未フィルタの結果を直列化する経路がコンパイル時に存在しない**。
//
// 名前が「Unfiltered」であることに意味がある。この型を手に持っている間は、まだ利用者へ見せて
// よい形になっていない。
internal sealed record UnfilteredSubgraph(
    IReadOnlyList<GraphDocument> Nodes,
    IReadOnlyList<Edge> Edges,
    bool Truncated,
    // FR-17, ADR-0049 決定 1・3 (#980): **許可済み**の総数。表示上限を超えて数えた結果である。
    //
    // 🔴 **数えてよいのは ABAC 判定を通過した品目だけ**（ADR-0049 決定 1）。不許可ノードは
    // 従前どおりその場で打ち切り、次ホップへ進まない（ADR-0034 決定 1 の具体化は改まっていない）。
    // したがって**中間結果に不許可文書は載らない** —— ADR-0034 が上限超過を禁じた理由は
    // 損なわれず、ADR-0049 はまさにその理由に規則を合わせた改定である。
    int TotalNodes = 0,
    int TotalEdges = 0,
    // ADR-0049 決定 3: 算出用の上限（2,000 / 5,000）に達したか。
    // **立っているとき総数は「以上」の意味になる。** 正確な数を出すために探索を伸ばさない。
    bool TotalIsLowerBound = false)
{
    public static UnfilteredSubgraph Empty { get; } = new([], [], false);
}
