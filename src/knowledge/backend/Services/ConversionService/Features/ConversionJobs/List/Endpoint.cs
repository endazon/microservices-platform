using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Features.ConversionJobs.List;

// FR-12, UC-06, SC-07, IADR-0042: 一覧（?status=failed 等で絞り込み。新しい順）。
internal static class ListConversionJobsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (IConversionJobStore store, string? status, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(status, ct)))
            .WithName("ConversionJobList").Produces<List<ConversionJobDto>>();
    }
}
