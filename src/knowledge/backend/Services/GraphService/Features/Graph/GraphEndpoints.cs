using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.Graph;

// FR-17, UC-10, ADR-0034: グラフ読み取りエンドポイント。
//
// 本単位（#908）が公開するのは**ホップ 0（起点ノード 1 件）**だけである。多ホップ探索・
// 上限 200/500・ホップ超過の拒否は #909 が足す。ホップ 0 でも認可の骨格はすべて通っており、
// deny-closed・存在秘匿・型ゲートがこの 1 本で実演される。
public static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/graph").WithTags("Graph");

        // FR-17, UC-10, ADR-0034 決定 1・2: 起点ノードを 1 件返す。
        //
        // 🔴 **非許可・レコード欠落・文書不存在をすべて同一の 404 に倒す。**
        // 403 と 404 を打ち分けると存在が漏れる。ADR-0034 は「利用者がリンク切れと権限不足を
        // 区別できないこと」を**受け入れ済みの副作用**として明記しており、本実装はその線に従う
        // （GraphEndpointsSecrecyTests が本文・ヘッダで区別できないことを固定する）。
        g.MapGet("/{documentId:guid}", async (
            Guid documentId,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            // ADR-0034: スコープはリクエストごとに 1 回だけ解決する。
            // 認可サービス障害時は Granted=false へ縮退する（GraphAccessResolver）。
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);

            // FR-05: deny-by-default。許可ポリシーが無ければ起点自体も見せない。
            if (!scope.Granted)
                return NotFound();

            var node = await store.FindNodeAsync(documentId, ct);

            // 「文書が無い」と「属性の複製がまだ届いていない」は同じ扱いになる
            // （IADR-0242 決定 12-3: 複製が無いノードは不可視）。
            if (node is null)
                return NotFound();

            // 🔴 **ここが唯一の構築経路である。** 非許可なら AuthorizedNode は作れない。
            var authorized = AuthorizedNode.Authorize(node, scope);
            if (authorized is null)
                return NotFound();

            // 本単位は辺を返さない（多ホップ探索は #909）。
            var subgraph = new UnfilteredSubgraph([authorized.Node], [], Truncated: false);

            // 🔴 **応答 DTO は Seal からしか作れない。** 未フィルタ結果は直列化できない。
            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNode")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status404NotFound);

        // FR-17, UC-10, ADR-0034 決定 1・2・3・4: 近傍探索（多ホップ）。
        //
        // 🔴 **hops 上限の超過は 400 で拒否する。黙って切り詰めない**（決定 3）。
        // 切り詰めると、利用者は「3 ホップ先まで見た」と思い込んだまま欠けた結果を受け取る。
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
                return NotFound();

            var start = await store.FindNodeAsync(documentId, ct);
            if (start is null)
                return NotFound();

            var origin = AuthorizedNode.Authorize(start, scope);
            if (origin is null)
                return NotFound();

            var subgraph = await traversal.ExploreAsync(origin, scope, requested, by, edgeTypes, ct);

            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNeighbors")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);

        // FR-17, ADR-0034 決定 8: 利用者が明示的に辺を付与する。
        //
        // 🔴 **作成時に到達可能性を検証する。** 決定 8 は「個人資料から権限外文書へのリンクは
        // 張れない。リンク作成時に権限を検証する」と定める。検証しないと、**辺を張る行為そのものが
        // 権限外文書の存在を確かめる手段**になる（張れたら在る、張れなければ無い）。
        //
        // **両端を検証する。** 終点だけ見ると、見えない文書を起点にして辺を生やせてしまう。
        // 辺の可視条件（両端が許可）と作成条件を揃える（IADR-0242 決定 6）。
        //
        // 🔴 **不可視・不存在はすべて 404。403 にしない** —— 403 は「権限が無いだけで存在はする」
        // ことを漏らす。読み取り側（存在秘匿）と同じ線を引く。
        //
        // 🔴 **read と write の 2 つのスコープを解決する**（#993 / IADR-0272 決定 2）。従前は
        // read のスコープ 1 本で「見えるか」と「書いてよいか」の両方を判定しており、
        // **「読めるなら書ける」形**だった。**同じ 1 回の解決で両方には答えられない** ——
        // ADR-0034 決定 8 の具体化は到達可能性の検証を「**閲覧**権限」と明示し、
        // ADR-0036 D-07 は書き込みを `doc.owner ∈ { ${current_user} }` で判定すると定める。
        g.MapPost("/edges", async (
            CreateGraphEdgeRequest req,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            GraphDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (req.SourceDocumentId == Guid.Empty || req.TargetDocumentId == Guid.Empty)
                return Results.BadRequest(new { error = "document_id_required" });
            if (req.SourceDocumentId == req.TargetDocumentId)
                return Results.BadRequest(new { error = "self_edge_not_allowed" });

            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted)
                return NotFound();

            // 型は先に引く。**存在しない型は 400**（文書の可視性とは無関係な入力誤りであり、
            // 404 にすると型の実在が文書の存在秘匿と混ざって読めなくなる）。
            var edgeType = await db.EdgeTypes.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == req.EdgeTypeId, ct);
            if (edgeType is null)
                return Results.BadRequest(new { error = "unknown_edge_type" });

            // ★到達可能性の検証★ —— 両端とも呼び出し主体のスコープで可視でなければならない。
            var source = await store.FindNodeAsync(req.SourceDocumentId, ct);
            var target = await store.FindNodeAsync(req.TargetDocumentId, ct);
            if (source is null || target is null)
                return NotFound();
            if (AuthorizedNode.Authorize(source, scope) is null
                || AuthorizedNode.Authorize(target, scope) is null)
                return NotFound();

            // ★書き込みの認可★ —— **変更の直前に置く**（#993 / IADR-0272 決定 2・3）。
            //
            // 🔴 **Granted だけを見ない。** 見ると write ポリシーの文書条件を捨てることになり、
            // 「public に限った write 権限で restricted の文書へ辺を張る」が通る。**解決した
            // スコープを使わないのは #993 と同型の欠陥である。** 述語は直接呼ばず、型ゲート
            // （AuthorizedNode.Authorize）を通す（IADR-0242 決定 2 の作法）。
            //
            // **起点にだけ要求する。** 終点へ write を課すと、ADR-0034 決定 8 が明らかに想定して
            // いる「個人資料から会社文書へリンクを張る」操作が成立しなくなる（同決定が終点に課す
            // のは閲覧権限である）。**計画に無い制約を足さない。**
            //
            // ⚠ **write ポリシーが 1 件も無い間、この経路は全件 404 になる**（deny-by-default。
            // 計画 FR-05「既定は拒否」の正しい帰結）。配備時に write ポリシーの登録が要る。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (AuthorizedNode.Authorize(source, writeScope) is null)
                return NotFound();

            var edge = Edge.Create(
                req.SourceDocumentId, req.TargetDocumentId, edgeType.Id,
                edgeType.IsSymmetric, EdgeProvenance.User);

            // 重複の事前検査。**対称型は正規化後の並びで突き合わせる**（Edge.Create が正規化済み）。
            // ⚠ InMemory は一意索引を強制しないため、ここが実質唯一の防壁になる（#941）。
            var duplicate = await db.Edges.AnyAsync(e =>
                e.SourceDocumentId == edge.SourceDocumentId
                && e.TargetDocumentId == edge.TargetDocumentId
                && e.EdgeTypeId == edge.EdgeTypeId
                && e.SourceAnchor == edge.SourceAnchor
                && e.TargetAnchor == edge.TargetAnchor, ct);
            if (duplicate)
                return Results.Conflict(new { error = "edge_exists" });

            db.Edges.Add(edge);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 検査と保存の間の競合。一意制約違反を素の 500 にせず 409 へ変換する。
                return Results.Conflict(new { error = "edge_exists" });
            }

            return Results.Created($"/graph/edges/{edge.Id}", new GraphEdgeCreatedDto(
                edge.Id, edge.SourceDocumentId, edge.TargetDocumentId,
                edge.EdgeTypeId, edge.Provenance));
        }).WithName("CreateGraphEdge")
          .RequireAuthorization()
          .Produces<GraphEdgeCreatedDto>(StatusCodes.Status201Created)
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound)
          .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    // ADR-0034 決定 2: **1 種類の 404 しか返さない。**
    // 本文・ヘッダに差が出ると、そこから存在の有無が読めてしまう。
    private static IResult NotFound() => Results.NotFound();
}
