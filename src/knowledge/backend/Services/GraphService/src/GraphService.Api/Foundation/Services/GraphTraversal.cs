using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Ports;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Api.Foundation.Services;

// FR-17, UC-10, ADR-0034 決定 1・2・3・4, IADR-0242: **ホップごと ABAC を守る近傍探索。**
//
// 🔴 **本クラスの全体が「判定を展開・計数より前に置く」という一点のためにある。**
// 逆順にすると (a) 非許可ノードが「橋」として働き権限外文書が浮上し、(b) 表示上限の計数が
// 権限外品目を数えて存在を漏らす。どちらも ADR-0034 が名指しで禁じた形である。
//
// 型の裏付け: フロンティアの要素型は `AuthorizedNode` であり、`IGraphStore.LoadIncidentEdgesAsync`
// はそれしか受け取らない。**非許可ノードから展開することは型として書けない。**
internal sealed class GraphTraversal(IGraphStore store)
{
    // ADR-0034 決定 3: 深さ 既定 2 / 上限 3。超過は**丸めずエラー**（本クラスは検証済みの値を受ける）。
    public const int DefaultHops = 2;
    public const int MaxHops = 3;

    // ADR-0034 決定 4: 表示上限。**述語を通過した品目に対してのみ計数する。**
    public const int MaxNodes = 200;
    public const int MaxEdges = 500;

    public async Task<UnfilteredSubgraph> ExploreAsync(
        AuthorizedNode origin,
        AccessScopeResponse scope,
        int hops,
        CancellationToken ct = default)
    {
        var visited = new HashSet<Guid> { origin.DocumentId };
        var nodes = new List<GraphDocument> { origin.Node };
        var edges = new List<Edge>();
        var emitted = new HashSet<Guid>();
        var truncated = false;

        var frontier = new List<AuthorizedNode> { origin };

        for (var depth = 1; depth <= hops && frontier.Count > 0 && !truncated; depth++)
        {
            // フロンティアに接続する辺を双方向に一括ロード（バックリンクを含む。決定的順序）。
            var incident = await store.LoadIncidentEdgesAsync(frontier, ct);
            if (incident.Count == 0)
                break;

            var frontierIds = frontier.Select(n => n.DocumentId).ToHashSet();

            // 未訪問の相手側だけを属性込みで一括ロードする。
            var unseen = new HashSet<Guid>();
            foreach (var e in incident)
            {
                var far = FarEnd(e, frontierIds);
                if (!visited.Contains(far))
                    unseen.Add(far);
            }

            var loaded = (await store.LoadNodesAsync(unseen, ct))
                .ToDictionary(n => n.DocumentId);

            var next = new List<AuthorizedNode>();

            foreach (var edge in incident)
            {
                if (emitted.Contains(edge.Id))
                    continue;

                var far = FarEnd(edge, frontierIds);

                if (visited.Contains(far))
                {
                    // 既に可視と判定済みのノードどうしを結ぶ辺。両端が許可済みなので出してよい。
                    if (edges.Count >= MaxEdges) { truncated = true; break; }
                    edges.Add(edge);
                    emitted.Add(edge.Id);
                    continue;
                }

                // ★ホップごと判定★ —— 展開にも計数にも先立つ。
                //   ノードレコードが無い場合（属性の複製が未到達）も不許可と同じく落とす（fail-closed）。
                if (!loaded.TryGetValue(far, out var node))
                    continue;
                var authorized = AuthorizedNode.Authorize(node, scope);
                if (authorized is null)
                    continue;

                // ここから先は「許可済み」だけを数える。権限外は上限に一切影響しない。
                //
                // 新しいノードを入れられないなら**辺も出さない**。片端が応答に無い辺を出すと、
                // Seal が落として辻褄は合うが、上限の意味が「見えている辺の数」からずれる。
                if (nodes.Count >= MaxNodes) { truncated = true; continue; }
                if (edges.Count >= MaxEdges) { truncated = true; break; }

                edges.Add(edge);
                emitted.Add(edge.Id);

                visited.Add(far);
                nodes.Add(node);
                next.Add(authorized);
            }

            frontier = next;
        }

        return new UnfilteredSubgraph(nodes, edges, truncated);
    }

    // フロンティア側でない端点。両端がフロンティアにある辺は target 側を「相手」とみなす
    // （どちらも訪問済みなので、どちらを選んでも可視性の判定結果は変わらない）。
    private static Guid FarEnd(Edge edge, HashSet<Guid> frontierIds)
        => frontierIds.Contains(edge.SourceDocumentId)
            ? edge.TargetDocumentId
            : edge.SourceDocumentId;
}
