using Microsoft.EntityFrameworkCore;
using WikiService.Api.Infrastructure;
using WikiService.Api.Services;

namespace WikiService.Api.Endpoints;

// FR-13, UC-07, ADR-0011, IADR-0013: Wiki ページ閲覧エンドポイント。
// 閲覧は自前の軽量な読み取り専用 API で提供する（Wiki.js は配備しない。IADR-0013 が
// ADR-0011 の Supersede を計画へ提案）。ABAC は本システムがソースオブトゥルース。閲覧経路
// （一覧・本文）でも deny-by-default の属性フィルタを適用し、権限外文書を一切露出しない（受け入れ基準②）。
public static class WikiEndpoints
{
    public static IEndpointRouteBuilder MapWikiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/wiki").WithTags("Wiki");

        // 一覧: 権限内のページのみ。Granted=false は空配列（deny-by-default）。
        g.MapGet("/pages", async (WikiDbContext db, IWikiAccessResolver resolver,
            HttpContext http, CancellationToken ct) =>
        {
            var scope = await resolver.ResolveAsync(http, ct);
            if (!scope.Granted)
                return Results.Ok(Array.Empty<WikiPageSummary>());

            // 属性は jsonb のため取得後にメモリ内で ABAC 評価する（検索側と同方針）。
            var pages = await db.Pages.OrderBy(p => p.Title).ToListAsync(ct);
            var visible = AbacPageFilter.Filter(pages, scope)
                .Select(p => new WikiPageSummary(p.Id, p.DocumentId, p.Title, p.Slug, p.Status, p.SyncedAt))
                .ToList();
            return Results.Ok(visible);
        });

        // 個別（slug）: 権限外・不存在はいずれも 404（存在を秘匿）。
        g.MapGet("/pages/{slug}", async (string slug, WikiDbContext db,
            IWikiAccessResolver resolver, HttpContext http, CancellationToken ct) =>
        {
            var scope = await resolver.ResolveAsync(http, ct);
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Slug == slug, ct);
            return page is not null && AbacPageFilter.Matches(page, scope)
                ? Results.Ok(page)
                : Results.NotFound();
        });

        // 個別（documentId）: 権限外・不存在はいずれも 404（存在を秘匿）。
        g.MapGet("/pages/by-doc/{documentId:guid}", async (Guid documentId, WikiDbContext db,
            IWikiAccessResolver resolver, HttpContext http, CancellationToken ct) =>
        {
            var scope = await resolver.ResolveAsync(http, ct);
            var page = await db.Pages.FirstOrDefaultAsync(p => p.DocumentId == documentId, ct);
            return page is not null && AbacPageFilter.Matches(page, scope)
                ? Results.Ok(page)
                : Results.NotFound();
        });

        return app;
    }
}

// 一覧表示用の軽量サマリ（本文 URI・属性は含めない）。
public record WikiPageSummary(
    Guid Id, Guid DocumentId, string Title, string Slug, string Status, DateTimeOffset SyncedAt);
