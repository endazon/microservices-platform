using GraphService.Domain;
using GraphService.Domain.Ports;

namespace GraphService.Features.Graph.Neighbors;

// FR-17, UC-10, ADR-0034 決定 1・2・3・4: 近傍探索（多ホップ）。
//
// 🔴 **hops 上限の超過は 400 で拒否する。黙って切り詰めない**（決定 3）。
// 切り詰めると、利用者は「3 ホップ先まで見た」と思い込んだまま欠けた結果を受け取る。
//
// 🔴 **本ハンドラの判定順は仕様である。** `hops` / `types` の検証は認可より前に置く
// （下の注記を参照）。ファイルを移しても順序を組み替えてはならない。
// `GraphEndpointsSecrecyTests` がこの帰結（本文・ヘッダで区別できないこと）を固定する。
internal static class GraphNeighborsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{documentId:guid}/neighbors", async (
            Guid documentId,
            int? hops,
            // FR-17, SC-18, ADR-0049 決定 4 (#980): 間引きの基準（distance 既定 / updated / degree）。
            // **未知の値・未指定は既定へ縮退する**（例外にしない。SearchModes / SearchSorts と同じ作法）
            // —— 綴りを 1 つ間違えただけで画面が壊れる形にしない。
            string? by,
            // FR-17, SC-18 (#917): 辺の型フィルタ（型 ID のカンマ区切り。未指定・空 = 絞らない）。
            // サーバ側で絞るのが仕様である（planning#446。クライアントで打ち切り後に絞ると範囲が狭まる）。
            string? types,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            GraphTraversal traversal,
            HttpContext http,
            CancellationToken ct) =>
        {
            // 🔴 **hops の検証は認可より前に置く。順序を入れ替えてはならない。**
            //
            // 入れ替えると存在秘匿が壊れる —— 認可を先にすると、権限外・不存在の文書は 404、
            // 可視の文書だけが 400 を返すようになり、**hops=4 を投げるだけで文書の存在が判る**。
            // hops の妥当性は文書に依存しないので、先に弾けば何も漏れない。
            //
            // ⚠ CodeQL の `cs/user-controlled-bypass` がここを high で指摘する（利用者入力が
            // 条件を制御しているため）。**バイパスではない** —— この分岐は要求を*拒否*するだけで、
            // 通過した場合の認可（スコープ解決・Authorize）は無条件に実行される。
            // 指摘に従って順序を変えると、上記のとおり実際の情報漏れを作ることになる。
            var requested = hops ?? GraphTraversal.DefaultHops;
            if (requested < 1 || requested > GraphTraversal.MaxHops)
                return Results.BadRequest(new
                {
                    error = "hops_out_of_range",
                    message = $"hops は 1〜{GraphTraversal.MaxHops} で指定する（既定 {GraphTraversal.DefaultHops}）。",
                });

            // FR-17, SC-18 (#917): 辺の型フィルタの検証。**hops と同じく認可より前に置く** ——
            // 後ろへ置くと、権限外・不存在の文書は 404、可視の文書だけが 400 を返すようになり、
            // 不正な types を投げるだけで文書の存在が判る（hops の注記と同じ理由）。
            // 形式不正（GUID として読めない要素）だけを 400 で拒む。**実在しない型 ID は拒まない** ——
            // 辺の型辞書は認証のみで全利用者へ公開済みの語彙であり（#962）、実在の有無は秘匿対象では
            // なく、単に 1 本も一致しないだけである（辞書の改廃と URL の共有が競合しても壊れない）。
            //
            // ⚠ CodeQL の `cs/user-controlled-bypass` は、**hops と同じ理由でここも high で指摘し得る**
            // （利用者入力が条件を制御しているため）。上の注記と同じく**バイパスではない** —— この分岐は
            // 要求を*拒否*するだけで、通過した場合の認可（スコープ解決・Authorize）は無条件に実行される。
            // 🔴 **指摘に従って認可の後ろへ動かすと、本物の情報漏れができる。** 注記を hops 側だけに
            // 置くと、CodeQL がこの行を指したときに読み手が警告へ辿り着けないため、ここにも書く。
            IReadOnlySet<Guid>? edgeTypes = null;
            if (!string.IsNullOrWhiteSpace(types))
            {
                var parsed = new HashSet<Guid>();
                foreach (var part in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Guid.TryParse(part, out var typeId))
                        return Results.BadRequest(new
                        {
                            error = "edge_type_filter_invalid",
                            message = "types は辺の型 ID（GUID）のカンマ区切りで指定する。",
                        });
                    parsed.Add(typeId);
                }
                if (parsed.Count > 0)
                    edgeTypes = parsed;
            }

            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted)
                return GraphEndpoints.NotFound();

            var start = await store.FindNodeAsync(documentId, ct);
            if (start is null)
                return GraphEndpoints.NotFound();

            var origin = AuthorizedNode.Authorize(start, scope);
            if (origin is null)
                return GraphEndpoints.NotFound();

            var subgraph = await traversal.ExploreAsync(origin, scope, requested, by, edgeTypes, ct);

            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNeighbors")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);
    }
}
