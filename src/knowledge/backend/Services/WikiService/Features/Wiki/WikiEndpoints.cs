using WikiService.Domain;
using WikiService.Domain.Ports;
using WikiService.Features.Wiki.GetPageByDocument;
using WikiService.Features.Wiki.GetPageBySlug;
using WikiService.Features.Wiki.ListPages;
using WikiService.Features.Wiki.SearchPages;

namespace WikiService.Features.Wiki;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0009: Wiki.js 前段の ABAC 認可ゲートウェイの登録表
// （ADR-0068 決定 1）。
//
// 閲覧・編集の実体は Wiki.js に委譲する（IADR-0020）。本エンドポイントは自前で本文を保持せず、
// ABAC（本システムが単一真実源）を Wiki.js への「到達可否」として強制する認可プロキシである。
//   - 一覧: deny-by-default の属性フィルタ（AbacPageFilter）で権限内メタデータのみを返す。
//   - 個別: ABAC 通過時のみ Wiki.js の本文をプロキシ取得して返す。権限外・不存在はいずれも 404 で
//     存在を秘匿する（IADR-0009 の意味論を継承）。
// Wiki.js への直接到達はネットワーク分離（IADR-0017）で塞ぎ、認可はゲートウェイに集約する。
// Wiki.js 側のページ/グループ権限は属性ベース細粒度判定の代替とはしない（ADR-0011）。
//
// ADR-0065 決定 2 / ADR-0068 決定 1: 各操作の処理は `Features/Wiki/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group と、
// 2 つの個別取得が共有するプロキシ判定・その応答形。
public static class WikiEndpoints
{
    public static IEndpointRouteBuilder MapWikiEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/wiki").WithTags("Wiki");

        ListWikiPagesEndpoint.Map(g);
        // UC-07 基本フロー 1「検索する」（#1126 / IADR-0335）。**`/pages/{slug}` より先に登録しない**
        // 理由は無い（`/search` は `/pages/...` と前置が違うので衝突しない）が、
        // 経路の並びは UC-07 の逐語（開く／検索する）と同じ順にしておく。
        SearchWikiPagesEndpoint.Map(g);
        GetWikiPageBySlugEndpoint.Map(g);
        GetWikiPageByDocumentEndpoint.Map(g);

        return app;
    }

    // 認可ゲートウェイの共通判定: ABAC 通過時のみ Wiki.js 本文を取得して返す。
    // 不存在・権限外・Wiki.js 未反映はいずれも 404（存在秘匿・IADR-0009）。
    // **2 つの個別取得（slug / documentId）が使う**ため 2 段目に残る（ADR-0068 決定 2）。
    internal static async Task<IResult> ProxyOrNotFoundAsync(
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

// 個別取得のゲートウェイ応答（メタデータ ＋ Wiki.js からプロキシした本文）。
// **2 つの個別取得が共有する**ため 2 段目に残る（ADR-0068 決定 2）。
public record WikiPageView(
    Guid Id, Guid DocumentId, string Title, string Slug, string WikiPath, string Status,
    DateTimeOffset SyncedAt, string Content);
