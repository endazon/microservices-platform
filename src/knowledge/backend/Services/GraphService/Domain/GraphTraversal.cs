using GraphService.Domain;
using GraphService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace GraphService.Domain;

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

    // FR-17, ADR-0049 決定 3 (#980): **総数の算出のための探索上限。** 表示上限とは別である。
    //
    // 🔴 ADR-0049 は ADR-0034 の「内部の探索も同じ上限で打ち切る」1 行だけを改めた。
    // **決定 1 の具体化（不許可ノードはその場で打ち切り、次ホップへ進まない）は改まっていない。**
    // むしろ ADR-0049 の前提である —— 許可済みだけを数える限り「中間結果に不許可文書が
    // 載らない」という理由は損なわれない、というのが改定の根拠だからである。
    //
    // **「上限を超えてよい」は「無制限でよい」ではない。** 算出用の上限にも達した場合は、
    // 総数を「以上」として示す（正確な数を出すために探索を伸ばさない）。
    //
    // ⚠️ **この値は実測ではない。** ADR-0049 自身が「表示上限の 10 倍を置いた。運用で不足または
    // 過大と判明した時点で見直す」と明記している。
    public const int CountingMaxNodes = 2_000;
    public const int CountingMaxEdges = 5_000;



    // FR-17, UC-10, ADR-0034, ADR-0049 (#980): 二段の上限で探索する。
    //
    // 🔴 **段が 2 つあることが本メソッドの要点である。**
    //   ① **探索**は算出用の上限（2,000 / 5,000）まで進み、**許可済みの総数**を数える
    //   ② **選抜**は表示上限（200 / 500）で、①の候補集合から間引き基準に従って選ぶ
    //
    // **①が無いと②の母集合が存在しない。** 200 で打ち切る実装には「上位 200 件」を選ぶ
    // 対象が無く、間引き基準（更新日順・次数順）が意味を持たない（ADR-0049 決定 4）。
    //
    // FR-17, SC-18 (#917): `edgeTypes` は辺の型フィルタ（null = 絞らない）。
    // 🔴 **絞りは探索の入口（候補へ入れる前）で適用する。サーバ側で絞るのが仕様である** ——
    // 表示上限で打ち切った後にクライアントで絞ると「フィルタ後の上位 200 件」ではなく
    // 「上位 200 件のうち一致したもの」になり、利用者が見る範囲が意図せず狭まる（planning#446。
    // 05_screens §SC-18「辺の種別フィルタはサーバ側で適用する」）。総数（TotalNodes / TotalEdges）も
    // フィルタ後の母集合で数える —— 帯の「全 N 件」は「いま選んでいる型での全数」を意味する。
    public async Task<UnfilteredSubgraph> ExploreAsync(
        AuthorizedNode origin,
        AccessScopeResponse scope,
        int hops,
        string? thinning = null,
        IReadOnlySet<Guid>? edgeTypes = null,
        CancellationToken ct = default)
    {
        var visited = new HashSet<Guid> { origin.DocumentId };

        // 🔴 **候補集合。判定を通過したものだけが入る。**
        // ADR-0049 決定 1 が「数えてよいのは ABAC 判定を通過した品目だけ」と明記しており、
        // その条件下でのみ ADR-0034 の「中間結果に不許可文書が載らない」という理由が保たれる。
        var nodes = new List<GraphDocument> { origin.Node };
        var edges = new List<Edge>();
        var emitted = new HashSet<Guid>();

        // 間引き基準の材料。**距離は到達順（挿入順）で表す**ので別に持たない。
        var degreeOf = new Dictionary<Guid, int>();
        var countingLimitHit = false;

        var frontier = new List<AuthorizedNode> { origin };

        for (var depth = 1; depth <= hops && frontier.Count > 0 && !countingLimitHit; depth++)
        {
            // フロンティアに接続する辺を双方向に一括ロード（バックリンクを含む。決定的順序）。
            IReadOnlyList<Edge> incident = await store.LoadIncidentEdgesAsync(frontier, ct);

            // FR-17, SC-18 (#917): 辺の型フィルタ。**候補へ入れる前（展開・計数・ノードのロードより前）に
            // 落とす。** ここより後ろに置くと、落とした辺が上限の計数や次ホップの展開に混ざり、
            // 「フィルタ後の上位 200 件」という意味が壊れる。
            if (edgeTypes is not null)
                incident = incident.Where(e => edgeTypes.Contains(e.EdgeTypeId)).ToList();

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

            // ADR-0035 決定 2 (#947a): 次数。**ABAC で絞らない**（ポートの注記参照）。
            // **ホップを跨いで累積する** —— 間引き基準 `degree` が全候補について要るためである。
            foreach (var (id, d) in await store.LoadDegreesAsync(unseen, ct))
                degreeOf[id] = d;

            var next = new List<AuthorizedNode>();

            foreach (var edge in incident)
            {
                if (emitted.Contains(edge.Id))
                    continue;

                var far = FarEnd(edge, frontierIds);

                if (visited.Contains(far))
                {
                    // 既に可視と判定済みのノードどうしを結ぶ辺。両端が許可済みなので出してよい。
                    if (edges.Count >= CountingMaxEdges) { countingLimitHit = true; break; }
                    edges.Add(edge);
                    emitted.Add(edge.Id);
                    continue;
                }

                // ★ホップごと判定★ —— 展開にも計数にも先立つ。
                //   ノードレコードが無い場合（属性の複製が未到達）も不許可と同じく落とす（fail-closed）。
                //
                // 🔴 **ADR-0049 はこの判定を改めていない。** 改めたのは「内部の探索も表示上限で
                // 打ち切る」の 1 行だけであり、決定 1 の具体化（不許可ノードはその場で打ち切り、
                // 次ホップへ進まない）は**むしろ本改定の前提**である。
                if (!loaded.TryGetValue(far, out var node))
                    continue;
                var authorized = AuthorizedNode.Authorize(node, scope);
                if (authorized is null)
                    continue;

                // ここから先は「許可済み」だけを数える。権限外は上限に一切影響しない。
                if (nodes.Count >= CountingMaxNodes) { countingLimitHit = true; continue; }
                if (edges.Count >= CountingMaxEdges) { countingLimitHit = true; break; }

                edges.Add(edge);
                emitted.Add(edge.Id);

                visited.Add(far);
                nodes.Add(node);

                // 🔴 ADR-0035 決定 2: ハブ文書は**中継点にしない**。
                //
                // **すでに nodes へ入れてある** —— 結果には現れる。ここで制御するのは
                // 「次のホップの起点になるか」だけである。**next へ入れないこと**が
                // 「中継点にしない」の意味であり、nodes から外すことではない。
                if (degreeOf.GetValueOrDefault(far) > MaxHubDegree)
                    continue;

                next.Add(authorized);
            }

            frontier = next;
        }

        // ── ② 選抜 ─────────────────────────────────────────────
        return Select(origin.DocumentId, nodes, edges, degreeOf, thinning, countingLimitHit);
    }

    // FR-17, SC-18, ADR-0049 決定 1・3・4 (#980): 候補集合から表示分を選ぶ。
    //
    // **総数は「間引く前の全体」であって「返した件数」ではない。** 両者が一致する小さな
    // データでは取り違えを検出できないため、試験はわざと食い違う母集合で測る（仕様書 T-01）。
    private static UnfilteredSubgraph Select(
        Guid originId,
        List<GraphDocument> nodes,
        List<Edge> edges,
        IReadOnlyDictionary<Guid, int> degreeOf,
        string? thinning,
        bool countingLimitHit)
    {
        var totalNodes = nodes.Count;
        var totalEdges = edges.Count;

        // 上限に収まっているなら選抜は要らない（**現行と同一の結果になる**）。
        if (totalNodes <= MaxNodes && totalEdges <= MaxEdges)
            return new UnfilteredSubgraph(nodes, edges, false, totalNodes, totalEdges, countingLimitHit);

        var by = GraphThinning.Normalize(thinning);

        // 🔴 **起点は必ず残す。** 起点が消えると「起点ありの近傍探索」が成立しない（SC-18 主要素 2）。
        var origin = nodes.First(n => n.DocumentId == originId);
        var rest = nodes.Where(n => n.DocumentId != originId);

        // **距離（既定）は到達順そのもの** —— BFS の挿入順が距離の昇順である。
        // 並べ替えは**安定**でなければならない（同値の並びが実行ごとに変わると打ち切りが非決定になる）。
        var ordered = by switch
        {
            GraphThinning.Updated => rest.OrderByDescending(n => n.UpdatedAt).ThenBy(n => n.DocumentId),
            GraphThinning.Degree => rest.OrderByDescending(n => degreeOf.GetValueOrDefault(n.DocumentId))
                                        .ThenBy(n => n.DocumentId),
            _ => rest.OrderBy(_ => 0),   // 到達順を保つ（OrderBy は安定ソート）
        };

        var selected = new List<GraphDocument> { origin };
        selected.AddRange(ordered.Take(MaxNodes - 1));
        var selectedIds = selected.Select(n => n.DocumentId).ToHashSet();

        // **両端が選抜に残った辺だけを出す。** 片端が欠けた辺を出すと、Seal が落として辻褄は
        // 合うが、上限の意味が「見えている辺の数」からずれる。
        var selectedEdges = edges
            .Where(e => selectedIds.Contains(e.SourceDocumentId) && selectedIds.Contains(e.TargetDocumentId))
            .Take(MaxEdges)
            .ToList();

        return new UnfilteredSubgraph(
            selected, selectedEdges,
            Truncated: true,
            TotalNodes: totalNodes,
            TotalEdges: totalEdges,
            TotalIsLowerBound: countingLimitHit);
    }

    // フロンティア側でない端点。両端がフロンティアにある辺は target 側を「相手」とみなす
    // （どちらも訪問済みなので、どちらを選んでも可視性の判定結果は変わらない）。
    private static Guid FarEnd(Edge edge, HashSet<Guid> frontierIds)
        => frontierIds.Contains(edge.SourceDocumentId)
            ? edge.TargetDocumentId
            : edge.SourceDocumentId;
}

// FR-17, SC-18, ADR-0049 決定 4 (#980): 表示上限を超えた候補集合から 200 件を選ぶときの基準。
//
// **未知の値・未指定は既定（distance）へ縮退する** —— `SearchModes` / `SearchSorts` と同じ作法。
// 例外にすると、綴りを 1 つ間違えただけで画面が壊れる。
public static class GraphThinning
{
    // 起点からの距離が近い順。**既定であり、現行（改定前）の挙動と同じ**。
    public const string Distance = "distance";
    // 更新日が新しい順。
    public const string Updated = "updated";
    // 次数が大きい順。
    public const string Degree = "degree";

    public static readonly string[] All = [Distance, Updated, Degree];

    public static bool IsValid(string? value)
        => value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value)
        => IsValid(value) ? value!.ToLowerInvariant() : Distance;
}
