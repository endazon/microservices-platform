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

    // FR-04, ADR-0035 決定 2 (#947a): **ハブ文書の次数上限。**
    //
    // 🔴 **意味は「展開の中継点にしない」であって「結果から除く」ではない。**
    // 除くと、利用者から見えるはずの関係が消える（「全社共通規程を参照している」という事実
    // 自体は正しい関係である）。狙いは**ハブ経由で無関係な文書が芋づる式に湧くこと**の抑制で
    // あって、ハブ自体の隠蔽ではない。
    //
    // 🔴 **この値は実測ではない。** ADR-0035 §結果 は「実データの次数分布を見て定める」と
    // 実装側へ委譲しているが、**その実データが無い**（取り込み経路が未配線。#911 / #912）。
    // 暫定値であり、実データが入った時点で測り直す（#947a 仕様書 §未決事項）。
    //
    // 暫定 50 の根拠: 表示上限（ノード 200）に対し 1 つのノードが結果の 1/4 を占めるあたりが
    // 「ハブ」の境目であること／ADR が挙げる例（全社共通規程・用語集）は数百から参照される
    // 想定であり 50 は確実に捕らえること／通常の文書を誤って中継点から外さないこと。
    public const int MaxHubDegree = 50;

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

            // ADR-0035 決定 2 (#947a): 次数を先に引く。**ABAC で絞らない**（ポートの注記参照）。
            // 引くのは「今回新しく見えた候補」だけでよい —— 既訪問はもう展開しないためである。
            var degrees = await store.LoadDegreesAsync(unseen, ct);

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

                // 🔴 ADR-0035 決定 2: ハブ文書は**中継点にしない**。
                //
                // **すでに nodes へ入れてある** —— 結果には現れる。ここで制御するのは
                // 「次のホップの起点になるか」だけである。**next へ入れないこと**が
                // 「中継点にしない」の意味であり、nodes から外すことではない。
                if (degrees.GetValueOrDefault(far) > MaxHubDegree)
                    continue;

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
