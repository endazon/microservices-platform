using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes.Rename;

// FR-17, SC-09, ADR-0033 決定 9: 改名。
// 🔴 **識別子は変えない。** 辺は識別子を参照しているため、既存の辺は 1 行も書き換わらずに
// 新しい名前へ追随する。ここで Id を振り直すと既存の参照が全部切れる。
internal static class RenameEdgeTypeEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPut("/{id:guid}", async (Guid id, RenameEdgeTypeRequest req, GraphDbContext db,
            CancellationToken ct) =>
        {
            var type = await db.EdgeTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (type is null) return Results.NotFound();

            var name = EdgeType.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name_required" });

            // 同じ名前への改名（実質の no-op）を 409 にしても管理者は何も直せない。
            if (!string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase)
                && await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                return EdgeTypeEndpoints.Conflict(name);

            type.Rename(name);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return EdgeTypeEndpoints.Conflict(name);
            }

            var usage = await EdgeTypeEndpoints.UsageOfAsync(db, type.Id, ct);
            return Results.Ok(new EdgeTypeDto(
                type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, usage));
        }).WithName("RenameEdgeType").Produces<EdgeTypeDto>();
    }
}
