namespace DataSourceService.Api.Endpoints;

public static class DataSourceEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/datasources").WithTags("DataSources");

        // FR-01, UC-04: データソース登録・同期スケルトン
        group.MapGet("/", () => Results.Ok(Array.Empty<object>())).WithName("GetDataSources");

        group.MapPost("/", (object dto) => Results.Accepted("/datasources/new", dto)).WithName("CreateDataSource");

        group.MapPost("/{id:guid}/sync", (Guid id) =>
            Results.Accepted($"/datasources/{id}/sync/job-1", new { jobId = "job-1", status = "queued" }))
            .WithName("SyncDataSource");

        group.MapGet("/{id:guid}", (Guid id) => Results.NotFound(new { message = $"DataSource {id} not found (stub)" }))
            .WithName("GetDataSource").Produces(404);
    }
}
