using Microsoft.EntityFrameworkCore;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki.GetPageBySlug;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0009: 個別（slug）。
// ABAC 通過時のみ Wiki.js 本文をプロキシ。権限外・不存在はいずれも 404（存在秘匿）。
// 🔴 FR-13, ADR-0091 決定 1・2, IADR-0644 (#2108): 深いリンクの同一性は DocumentId（`/pages/by-doc/{id}`）であり、この経路は
// 旧形式の共有 URL（`?page=<slug>`）の互換の解決だけに残す。slug は一意でない（一意索引を落とした）ので、
// **同じ slug の行が 2 つ以上あれば本文を返さない**（どれか 1 件を選ぶと、利用者は別の文書が出たことに気づけない）。
// 数えるのは状態・権限を問わない全行 —— 権限内の 1 件だけを返すと、同名の権限外の文書があるかが 200 / 404 の違いで漏れる。
// 応答は不存在・権限外と同じ 404（「複数ある」と教えない。IADR-0009）。
internal static class GetWikiPageBySlugEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/pages/{slug}", async (string slug, WikiDbContext db, IWikiAccessResolver resolver,
            IWikiJsClient wikiJs, HttpContext http, CancellationToken ct) =>
        {
            var matches = await db.Pages.Where(p => p.Slug == slug).Take(2).ToListAsync(ct);
            var page = matches.Count == 1 ? matches[0] : null;
            return await WikiEndpoints.ProxyOrNotFoundAsync(page, resolver, wikiJs, http, ct);
        });
    }
}
