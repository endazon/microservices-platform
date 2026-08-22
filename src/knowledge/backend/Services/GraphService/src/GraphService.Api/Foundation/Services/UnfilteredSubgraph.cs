using GraphService.Api.Foundation.Domain;

namespace GraphService.Api.Foundation.Services;

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
    bool Truncated)
{
    public static UnfilteredSubgraph Empty { get; } = new([], [], false);
}
