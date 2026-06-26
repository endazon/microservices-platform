using Microsoft.EntityFrameworkCore;
using WikiService.Api.Infrastructure;

namespace WikiService.Api.Endpoints;

// FR-13, UC-07: Wiki ページ閲覧エンドポイント（ABAC は RetrievalService で適用済み）
public static class WikiEndpoints
{
    public static IEndpointRouteBuilder MapWikiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/wiki").WithTags("Wiki");

        g.MapGet("/pages", async (WikiDbContext db) =>
            Results.Ok(await db.Pages
                .OrderBy(p => p.Title)
                .Select(p => new { p.Id, p.DocumentId, p.Title, p.Slug, p.Status, p.SyncedAt })
                .ToListAsync()));

        g.MapGet("/pages/{slug}", async (string slug, WikiDbContext db) =>
        {
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Slug == slug);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });

        g.MapGet("/pages/by-doc/{documentId:guid}", async (Guid documentId, WikiDbContext db) =>
        {
            var page = await db.Pages.FirstOrDefaultAsync(p => p.DocumentId == documentId);
            return page is null ? Results.NotFound() : Results.Ok(page);
        });

        return app;
    }
}
