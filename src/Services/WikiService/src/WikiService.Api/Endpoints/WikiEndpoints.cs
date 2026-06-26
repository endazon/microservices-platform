namespace WikiService.Api.Endpoints;

public static class WikiEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/wiki").WithTags("Wiki");
        // FR-13, UC-07, ADR-0011: Wiki.js 連携スケルトン
        group.MapGet("/pages", () => Results.Ok(Array.Empty<object>())).WithName("GetWikiPages");
        group.MapGet("/pages/{slug}", (string slug) =>
            Results.NotFound(new { message = $"Page {slug} not found (stub)" }))
            .WithName("GetWikiPage").Produces(404);
        group.MapPost("/sync/{documentId:guid}", (Guid documentId) =>
            Results.Accepted($"/wiki/sync/{documentId}", new { status = "queued (stub)" }))
            .WithName("SyncDocumentToWiki");
    }
}
