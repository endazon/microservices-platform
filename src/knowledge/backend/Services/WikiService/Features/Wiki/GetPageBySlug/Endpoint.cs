using Microsoft.EntityFrameworkCore;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki.GetPageBySlug;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0009: 個別（slug）。
// ABAC 通過時のみ Wiki.js 本文をプロキシ。権限外・不存在はいずれも 404（存在秘匿）。
internal static class GetWikiPageBySlugEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/pages/{slug}", async (string slug, WikiDbContext db, IWikiAccessResolver resolver,
            IWikiJsClient wikiJs, HttpContext http, CancellationToken ct) =>
        {
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Slug == slug, ct);
            return await WikiEndpoints.ProxyOrNotFoundAsync(page, resolver, wikiJs, http, ct);
        });
    }
}
