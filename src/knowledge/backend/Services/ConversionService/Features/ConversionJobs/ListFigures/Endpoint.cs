using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Features.ConversionJobs.ListFigures;

// UC-06, SC-07, IADR-0154: 人手補正 Phase 1 の本文取得。2 ペイン（左＝図コード・右＝元の図画像）
// が要る材料を返す。**画像のバイト列は返さない**——一覧が図の枚数だけ膨らむため、BFF が
// 別の口で配信する（IADR-0154 決定 2）。
internal static class ListConversionFiguresEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/figures", async (Guid id, IConversionJobStore store, CancellationToken ct) =>
        {
            var figures = await store.ListFiguresAsync(id, ct);
            return figures is null ? Results.NotFound() : Results.Ok(figures);
        }).WithName("ConversionJobFigureList").Produces<List<ConversionFigureDto>>();
    }
}
