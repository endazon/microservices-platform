using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes.Delete;

// FR-17, SC-09, ADR-0033 決定 9: 削除。**参照が 1 件でもあれば拒否する。**
//
// 🔴 **数え方は一覧と同一でなければならない。** 一覧が 0 件と表示したのに削除が 409 を
// 返す（あるいはその逆）と、管理者は辞書を信用できなくなる。同じ `UsageOfAsync` を使う。
//
// DB 層にも `ON DELETE RESTRICT` があり、これが最後の防壁になる。**その例外を素の 500 で
// 漏らさない** —— アプリ層の事前カウントをすり抜けた競合も 409 で返す。
internal static class DeleteEdgeTypeEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapDelete("/{id:guid}", async (Guid id, GraphDbContext db, CancellationToken ct) =>
        {
            var type = await db.EdgeTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (type is null) return Results.NotFound();

            var usage = await EdgeTypeEndpoints.UsageOfAsync(db, id, ct);
            if (usage > 0)
                return Results.Conflict(new EdgeTypeInUseResponse(
                    "edge_type_in_use",
                    $"型「{type.Name}」は {usage} 本の辺で使われているため削除できません。",
                    usage));

            db.EdgeTypes.Remove(type);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // ON DELETE RESTRICT に弾かれた（事前カウントと削除の間に辺が張られた）。
                var now = await EdgeTypeEndpoints.UsageOfAsync(db, id, ct);
                return Results.Conflict(new EdgeTypeInUseResponse(
                    "edge_type_in_use",
                    $"型「{type.Name}」は {now} 本の辺で使われているため削除できません。",
                    now));
            }

            return Results.NoContent();
        }).WithName("DeleteEdgeType").Produces(StatusCodes.Status204NoContent);
    }
}
