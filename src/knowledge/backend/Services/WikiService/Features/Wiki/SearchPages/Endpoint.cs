using Microsoft.EntityFrameworkCore;
using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.ExternalServices;
using WikiService.Infrastructure.Persistence;

namespace WikiService.Features.Wiki.SearchPages;

// UC-07 基本フロー 1「**検索する**」, FR-13, FR-05, ADR-0011, IADR-0009, IADR-0020, IADR-0334:
// Wiki 前段の検索。**全文検索は Wiki.js へ委譲し、到達可否は前段の ABAC が決める**。
//
// なぜこの形か（IADR-0334）:
//   - **本文は前段が持たない**（IADR-0020: ゲートウェイは本文を自前で保持しない）。台帳（`WikiPage`）が
//     持つのは表題・スラッグ・タグ・属性だけなので、前段だけで検索すると本文に当たらない。
//   - 一方 ADR-0011 は「**Wiki.js 側のページ／グループ権限を属性ベース細粒度判定の代替としない**」と
//     定めている。よって Wiki.js が返したヒットは**そのままでは 1 件も見せられない**。
//   → 「委譲して、前段で絞り直す」。本文取得（`ProxyOrNotFoundAsync`）と同じ形の検索版である。
//
// 存在秘匿（IADR-0009）: 権限が無い／該当が無い／台帳に無いページは、いずれも**同じ 200 ＋ 空**に見える。
// **ただし「壊れている」は別の軸である** —— Wiki.js へ到達できない場合は 502 を返し、200 ＋ 空で隠さない。
internal static class SearchWikiPagesEndpoint
{
    // 過大要求の抑止（SearchBffEndpoints と同じ既定・上限の置き方）。
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/search", async (string? q, int? limit, WikiDbContext db, IWikiAccessResolver resolver,
            IWikiJsSearchClient wikiJs, ILoggerFactory loggerFactory, HttpContext http, CancellationToken ct) =>
        {
            // FR-05: deny-by-default。許可ポリシーが無ければ後段を叩かずに空を返す。
            var scope = await resolver.ResolveAsync(http, ct);
            if (!scope.Granted)
                return Results.Ok(Array.Empty<WikiSearchHit>());

            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(Array.Empty<WikiSearchHit>());

            var take = Math.Clamp(limit is null or <= 0 ? DefaultLimit : limit.Value, 1, MaxLimit);

            IReadOnlyList<WikiJsSearchHit> raw;
            try
            {
                raw = await wikiJs.SearchAsync(q, ct);
            }
            catch (Exception ex) when (ex is WikiJsSyncException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // **障害を 200 ＋ 空で隠さない。** 存在秘匿が区別させないのは「権限が無い」と「該当が
                // 無い」であって、「後段が壊れている」は別の軸である（IADR-0256 と同じ切り分け）。
                // 502 は文書について何も語らないので、秘匿は崩れない。
                loggerFactory.CreateLogger(typeof(SearchWikiPagesEndpoint))
                    .LogWarning(ex, "Wiki.js search unavailable");
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            // Wiki.js のパスから台帳の行を引き当てる。**`doc/<guid>` 以外は落とす**
            // （人手で作られたページは ABAC 判定の足場を持たない → 不可視）。
            var order = new List<Guid>();
            foreach (var hit in raw)
            {
                if (WikiPage.TryParseDocumentId(hit.Path, out var id) && !order.Contains(id))
                    order.Add(id);
            }
            if (order.Count == 0)
                return Results.Ok(Array.Empty<WikiSearchHit>());

            // Issue #88: アーカイブ済みは権限があっても現れない（一覧・個別と同じ意味論）。
            var pages = await db.Pages
                .Where(p => p.Status == WikiPageStatus.Active && order.Contains(p.DocumentId))
                .ToListAsync(ct);

            // FR-05, FR-19, IADR-0253: 属性は jsonb のため取得後にメモリ内で ABAC 評価する（一覧と同方針）。
            var visible = AbacPageFilter.Filter(pages, scope).ToDictionary(p => p.DocumentId);

            // **Wiki.js の関連度順を保つ**（台帳の並びで上書きしない）。
            var results = order
                .Where(visible.ContainsKey)
                .Take(take)
                .Select(id => visible[id])
                .Select(p => new WikiSearchHit(p.Id, p.DocumentId, p.Title, p.Slug, p.WikiPath, p.SyncedAt))
                .ToList();

            return Results.Ok(results);
        }).WithName("SearchWikiPages");
    }
}

// 検索結果の 1 件（本文は含めない。本文は個別取得が ABAC 通過後にプロキシする）。
// **表題・スラッグは台帳を正とする** —— Wiki.js 側の写しを応答へ載せない（IADR-0021 と同じ分界）。
// **検索だけが使う形である**ため、この操作フォルダに置く（ADR-0068 決定 2）。
public record WikiSearchHit(
    Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, DateTimeOffset SyncedAt);
