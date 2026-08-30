using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes.List;

// FR-17, SC-09, SC-10: 使用件数つきの一覧。
// **SC-10 の型別使用件数はこの 1 本が供給する。** 別経路を作ると数えの二重化が起きる。
internal static class ListEdgeTypesEndpoint
{
    internal static void Map(RouteGroupBuilder read)
    {
        read.MapGet("/", async (GraphDbContext db, CancellationToken ct) =>
            Results.Ok(await LoadWithUsageAsync(db, ct)))
            .WithName("ListEdgeTypes").Produces<List<EdgeTypeDto>>();
    }

    private static async Task<List<EdgeTypeDto>> LoadWithUsageAsync(GraphDbContext db, CancellationToken ct)
    {
        var types = await db.EdgeTypes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        if (types.Count == 0) return [];

        // 型ごとの件数を 1 クエリで集計する（型の数だけ COUNT を投げない）。
        var usage = await db.Edges.AsNoTracking()
            .GroupBy(e => e.EdgeTypeId)
            .Select(g => new { TypeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TypeId, x => x.Count, ct);

        return types
            .Select(t => new EdgeTypeDto(
                t.Id, t.Name, t.Layer, t.IsSymmetric, t.IsSeed, usage.GetValueOrDefault(t.Id)))
            .ToList();
    }
}
