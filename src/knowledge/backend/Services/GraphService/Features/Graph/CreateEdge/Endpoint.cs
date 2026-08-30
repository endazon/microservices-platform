using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.Graph.CreateEdge;

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
internal static class CreateGraphEdgeEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
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
                return GraphEndpoints.NotFound();

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
                return GraphEndpoints.NotFound();
            if (AuthorizedNode.Authorize(source, scope) is null
                || AuthorizedNode.Authorize(target, scope) is null)
                return GraphEndpoints.NotFound();

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
                return GraphEndpoints.NotFound();

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
    }
}
