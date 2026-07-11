using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using WikiService.Api.Foundation.Domain;
using WikiService.Api.Foundation.Persistence;
using WikiService.Api.Foundation.Ports;
using WikiService.Api.Foundation.Services;
using WikiService.Api.Composable.Adapters;

namespace WikiService.Api.Composable.Steps;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0021: 文書更新イベントを受信し Wiki.js へ同期する。
//
// 責務（IADR-0020 で「同期・統合・ABAC ゲートウェイ」に縮退）:
//   1. 正規化 Markdown 本文を MarkdownUri から取得し、Wiki.js へ GraphQL push（IADR-0021。閲覧・編集の実体）。
//   2. ABAC 判定用メタデータ（属性/タグ/slug/status）を wiki_svc に upsert する。これは Wiki.js 前段
//      ゲートウェイ（WikiEndpoints）が deny-by-default 属性フィルタ・404 存在秘匿を強制するための
//      「同期メタデータ」であり、認可の単一真実源（IADR-0021: 認可属性は Wiki.js に持ち込まない）。
//
// 冪等性: DocumentId 由来の安定パス（WikiPage.WikiPath）で upsert するため、再配信に対して冪等。
// 失敗時: Wiki.js push・本文取得の失敗は例外を送出し、MassTransit のリトライ／デッドレター
//   （UsePlatformRetry）へ委ねる。
public class DocumentSyncConsumer(
    WikiDbContext db,
    IWikiJsClient wikiJs,
    IWikiContentReader contentReader,
    ILogger<DocumentSyncConsumer> logger) : IConsumer<DocumentUpdated>, IPipelineStep
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "wiki-sync";

    public async Task Consume(ConsumeContext<DocumentUpdated> ctx)
    {
        var ev = ctx.Message;

        // Issue #88: アーカイブ（非公開化）の伝播。Wiki.js ページを unpublish + private にし、
        // メタデータを Archived にする（ゲートウェイの一覧・個別から不可視）。メタデータ未同期でも
        // Wiki.js 側の非公開化は正準パス（DocumentId 由来）で試みる（冪等・deny-closed）。
        if (ev.Status == "archived")
        {
            await wikiJs.ArchivePageAsync(WikiPage.PathFor(ev.DocumentId), ctx.CancellationToken);

            var archivedPage = await db.Pages
                .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ctx.CancellationToken);
            if (archivedPage is not null)
            {
                archivedPage.Archive();
                await db.SaveChangesAsync(ctx.CancellationToken);
            }
            logger.LogInformation("Archived document {DocumentId} on Wiki.js", ev.DocumentId);
            return;
        }

        if (ev.Status != "published" && ev.Status != "normalized") return;

        // 1) ABAC 同期メタデータの upsert（ゲートウェイのフィルタ用・単一真実源）。
        var existing = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ctx.CancellationToken);

        var page = existing;
        if (page is null)
        {
            page = WikiPage.CreateFromDocument(ev.DocumentId, ev.Title,
                ev.MarkdownUri, ev.Attributes, ev.Tags);
            db.Pages.Add(page);
        }
        else
        {
            page.Sync(ev.Title, ev.MarkdownUri, ev.Attributes, ev.Tags);
        }

        // 2) 正規化 Markdown 本文を取得し、Wiki.js へ冪等 push（閲覧・編集の実体を Wiki.js に委譲）。
        //    認可属性（Attributes）は push しない（IADR-0021: 認可は本システムが単一真実源）。
        //    多層防御（ADR-0011/IADR-0021）: 機密区分由来の粗粒度な非公開設定のみを Wiki.js へ伝える。
        //    ネットワーク分離（IADR-0017）が退行しても public 以外が無条件公開にならないよう、public
        //    以外（欠落含む）は Wiki.js 上でも非公開にする（deny-closed）。ABAC の代替ではない。
        var isPublic = ev.Attributes.TryGetValue("confidentiality", out var confidentiality)
            && string.Equals(confidentiality, "public", StringComparison.OrdinalIgnoreCase);
        var markdown = await contentReader.ReadAsync(ev.MarkdownUri, ev.Title, ctx.CancellationToken);
        await wikiJs.UpsertPageAsync(
            new WikiJsPage(page.WikiPath, ev.Title, markdown, ev.Tags, IsPrivate: !isPublic),
            ctx.CancellationToken);

        await db.SaveChangesAsync(ctx.CancellationToken);
        logger.LogInformation("Synced document {DocumentId} to Wiki.js at {Path}", ev.DocumentId, page.WikiPath);
    }
}
