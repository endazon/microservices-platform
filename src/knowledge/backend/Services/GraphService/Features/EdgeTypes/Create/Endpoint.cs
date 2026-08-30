using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes.Create;

// FR-17, SC-09: 追加。**同名は 409**（正規化後の名前で比較する）。
internal static class CreateEdgeTypeEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/", async (CreateEdgeTypeRequest req, GraphDbContext db, CancellationToken ct) =>
        {
            var name = EdgeType.Normalize(req.Name ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name_required" });
            if (!EdgeTypeLayer.IsValid(req.Layer ?? string.Empty))
                return Results.BadRequest(new { error = "invalid_layer" });

            if (await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                return EdgeTypeEndpoints.Conflict(name);

            var type = EdgeType.Create(name, req.Layer!, req.IsSymmetric);
            db.EdgeTypes.Add(type);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 事前確認と保存の間に別の要求が同名を入れた（race）。一意制約違反を
                // **素の 500 にせず 409 へ変換する** —— 契約は「重複は 409」である。
                db.Entry(type).State = EntityState.Detached;
                if (await EdgeTypeEndpoints.ExistsAsync(db, name, ct))
                    return EdgeTypeEndpoints.Conflict(name);
                throw;
            }

            return Results.Created($"/graph/edge-types/{type.Id}",
                new EdgeTypeDto(type.Id, type.Name, type.Layer, type.IsSymmetric, type.IsSeed, 0));
        }).WithName("CreateEdgeType").Produces<EdgeTypeDto>(StatusCodes.Status201Created);
    }
}
