using Microsoft.EntityFrameworkCore;
using WikiService.Api.Infrastructure;
using WikiService.Api.Services;

namespace WikiService.Api.Endpoints;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0009: Wiki ページ閲覧エンドポイント。
// 目標構成（IADR-0020）: 閲覧・編集の実体は Wiki.js に委譲し、本エンドポイントは Wiki.js 前段の
// ABAC ゲートウェイ（認可プロキシ）へ改修する。ABAC は本システムがソースオブトゥルースであり、
// deny-by-default の属性フィルタ（AbacPageFilter）と 404 存在秘匿（IADR-0009）を Wiki.js への到達可否として強制する。
// 段階導入（IADR-0020 段2）: 現行は自前 wiki_svc に対する読み取り API が暫定的に強制点を担う。プロキシ化・
// wiki_svc 閲覧スキーマ撤去は後続 PR（要 PoC/ビルド環境）。旧 IADR-0013（Wiki.js 非配備）は Superseded。
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
