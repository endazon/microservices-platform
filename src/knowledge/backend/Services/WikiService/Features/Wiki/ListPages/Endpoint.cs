using Microsoft.EntityFrameworkCore;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki.ListPages;

// FR-13, UC-07, ADR-0011, IADR-0020: 一覧（権限内のページのみ）。
// Granted=false は空配列（deny-by-default）。
internal static class ListWikiPagesEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/pages", async (WikiDbContext db, IWikiAccessResolver resolver,
            HttpContext http, CancellationToken ct) =>
        {
            var scope = await resolver.ResolveAsync(http, ct);
            if (!scope.Granted)
                return Results.Ok(Array.Empty<WikiPageSummary>());

            // 属性は jsonb のため取得後にメモリ内で ABAC 評価する（検索側と同方針）。
            // Issue #88: アーカイブ済み（非公開化）ページは権限があっても一覧に出さない。
            var pages = await db.Pages
                .Where(p => p.Status == WikiPageStatus.Active)
                .OrderBy(p => p.Title).ToListAsync(ct);
            var visible = AbacPageFilter.Filter(pages, scope)
                .Select(p => new WikiPageSummary(p.Id, p.DocumentId, p.Title, p.Slug, p.WikiPath, p.Status, p.SyncedAt))
                .ToList();
            return Results.Ok(visible);
        });
    }
}

// 一覧表示用の軽量サマリ（本文・属性は含めない）。WikiPath は Wiki.js 上の閲覧パス。
// **一覧だけが使う形である**ため、この操作フォルダに置く（ADR-0068 決定 2）。
public record WikiPageSummary(
    Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, string Status, DateTimeOffset SyncedAt);
