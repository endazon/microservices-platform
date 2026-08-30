using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace ConversionService.Features.ConversionJobs.GetById;

// FR-12, UC-06, SC-07, IADR-0042: 個別取得。
internal static class GetConversionJobEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}", async (Guid id, IConversionJobStore store, CancellationToken ct) =>
        {
            var job = await store.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        }).WithName("ConversionJobGet").Produces<ConversionJobDto>();
    }
}
