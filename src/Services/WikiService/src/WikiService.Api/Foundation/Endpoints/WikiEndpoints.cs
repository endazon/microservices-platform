using Microsoft.EntityFrameworkCore;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;

namespace WikiService.Api.Foundation.Endpoints;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0009: Wiki.js 前段の ABAC 認可ゲートウェイ。
//
// 閲覧・編集の実体は Wiki.js に委譲する（IADR-0020）。本エンドポイントは自前で本文を保持せず、
// ABAC（本システムが単一真実源）を Wiki.js への「到達可否」として強制する認可プロキシである。
//   - 一覧: deny-by-default の属性フィルタ（AbacPageFilter）で権限内メタデータのみを返す。
//   - 個別: ABAC 通過時のみ Wiki.js の本文をプロキシ取得して返す。権限外・不存在はいずれも 404 で
//     存在を秘匿する（IADR-0009 の意味論を継承）。
// Wiki.js への直接到達はネットワーク分離（IADR-0017）で塞ぎ、認可はゲートウェイに集約する。
// Wiki.js 側のページ/グループ権限は属性ベース細粒度判定の代替とはしない（ADR-0011）。
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
            // Issue #88: アーカイブ済み（非公開化）ページは権限があっても一覧に出さない。
            var pages = await db.Pages
                .Where(p => p.Status == WikiPageStatus.Active)
                .OrderBy(p => p.Title).ToListAsync(ct);
            var visible = AbacPageFilter.Filter(pages, scope)
                .Select(p => new WikiPageSummary(p.Id, p.DocumentId, p.Title, p.Slug, p.WikiPath, p.Status, p.SyncedAt))
                .ToList();
            return Results.Ok(visible);
        });

        // 個別（slug）: ABAC 通過時のみ Wiki.js 本文をプロキシ。権限外・不存在はいずれも 404（存在秘匿）。
        g.MapGet("/pages/{slug}", async (string slug, WikiDbContext db, IWikiAccessResolver resolver,
            IWikiJsClient wikiJs, HttpContext http, CancellationToken ct) =>
        {
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Slug == slug, ct);
            return await ProxyOrNotFoundAsync(page, resolver, wikiJs, http, ct);
        });

        // 個別（documentId）: ABAC 通過時のみ Wiki.js 本文をプロキシ。権限外・不存在は 404（存在秘匿）。
        g.MapGet("/pages/by-doc/{documentId:guid}", async (Guid documentId, WikiDbContext db,
            IWikiAccessResolver resolver, IWikiJsClient wikiJs, HttpContext http, CancellationToken ct) =>
        {
            var page = await db.Pages.FirstOrDefaultAsync(p => p.DocumentId == documentId, ct);
            return await ProxyOrNotFoundAsync(page, resolver, wikiJs, http, ct);
        });

        return app;
    }

    // 認可ゲートウェイの共通判定: ABAC 通過時のみ Wiki.js 本文を取得して返す。
    // 不存在・権限外・Wiki.js 未反映はいずれも 404（存在秘匿・IADR-0009）。
    private static async Task<IResult> ProxyOrNotFoundAsync(
        WikiPage? page, IWikiAccessResolver resolver, IWikiJsClient wikiJs,
        HttpContext http, CancellationToken ct)
    {
        var scope = await resolver.ResolveAsync(http, ct);
        // Issue #88: アーカイブ済みページは権限があっても 404（存在秘匿の意味論を維持・IADR-0009）。
        if (page is null || page.Status != WikiPageStatus.Active || !AbacPageFilter.Matches(page, scope))
            return Results.NotFound();

        var content = await wikiJs.GetRenderedContentAsync(page.WikiPath, ct);
        if (content is null)
            return Results.NotFound();

        return Results.Ok(new WikiPageView(
            page.Id, page.DocumentId, page.Title, page.Slug, page.WikiPath, page.Status, page.SyncedAt, content));
    }
}

// 一覧表示用の軽量サマリ（本文・属性は含めない）。WikiPath は Wiki.js 上の閲覧パス。
public record WikiPageSummary(
    Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, string Status, DateTimeOffset SyncedAt);

// 個別取得のゲートウェイ応答（メタデータ ＋ Wiki.js からプロキシした本文）。
public record WikiPageView(
    Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, string Status,
    DateTimeOffset SyncedAt, string Content);
