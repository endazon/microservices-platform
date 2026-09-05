using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using WikiService.Domain;
using WikiService.Infrastructure.Persistence;
using WikiService.Domain.Ports;
using WikiService.Infrastructure.ExternalServices;

namespace WikiService.Features.Wiki.SyncDocument;

// FR-13, UC-07, ADR-0011, IADR-0020, IADR-0021: 文書更新イベントを受信し Wiki.js へ同期する。
//
// 責務（IADR-0020 で「同期・統合・ABAC ゲートウェイ」に縮退）:
//   1. 正規化 Markdown 本文を MarkdownUri から取得し、Wiki.js へ GraphQL push（IADR-0021。閲覧・編集の実体）。
//   2. ABAC 判定用メタデータ（属性/タグ/slug/status）を wiki_svc に upsert する。これは Wiki.js 前段
//      ゲートウェイ（WikiEndpoints）が deny-by-default 属性フィルタ・404 存在秘匿を強制するための
//      「同期メタデータ」であり、認可の単一真実源（IADR-0021: 認可属性は Wiki.js に持ち込まない）。
//
// 冪等性: DocumentId 由来の安定パス（WikiPage.WikiPath）で upsert するため、再配信に対して冪等。
// 失敗時: Wiki.js push・本文取得の失敗は例外を送出し、Wolverine のリトライ／デッドレター
//   （UsePlatformMessagingDefaults）へ委ねる。
//
// 🔴 ADR-0027 / E3b: **購読は Wolverine へ移した**（IPipelineStep<DocumentUpdated>・IADR-0239）。
// wiki-delete 段（E3a）と併せて、本サービスから MassTransit は撤去済みである。
public class DocumentSyncConsumer(
    WikiDbContext db,
    IWikiJsClient wikiJs,
    IWikiContentReader contentReader,
    ILogger<DocumentSyncConsumer> logger) : IPipelineStep<DocumentUpdated>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "wiki-sync";

    // FR-19, ADR-0046 D-01, ADR-0054 決定 1・2: 個人資料かどうかを判定する軸。
    // 属性キー・値の綴りは計画 ADR-0054 が確定させたものをそのまま用いる（実装で言い換えない）。
    private const string DocScopeKey = "doc_scope";
    private const string PrivateNoteScope = "private-note";

    // 🔴 **集合帰属で判定する。「organization でない」で書いてはならない。**
    // doc_scope は 2026-08-22 新設で実データ 0 件であり、既存 2,368 件へ遡及付与しない方針
    // （ADR-0054 §結果）。否定で書くと属性を持たない既存文書がすべて該当し、**組織文書の同期が
    // 一斉に止まる**。ADR-0036 D-04 が評価の性質を「集合帰属」と定めているのと同じ理由である。
    //
    // **この 2 つは動作で見分けがつかない** —— 個人資料が実データに 1 件も無い現在、どちらも
    // 「個人資料を同期しない」。分けられるのは「doc_scope を持たない文書が同期される」という
    // 陽性対照テストだけである（DocumentSyncConsumerTests）。
    private static bool IsPrivateNote(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(DocScopeKey, out var scope)
            && string.Equals(scope, PrivateNoteScope, StringComparison.OrdinalIgnoreCase);

    // ADR-0027 / E3b: Wolverine のハンドラ。
    public async Task Handle(DocumentUpdated ev, CancellationToken ct)
    {
        // Issue #88: アーカイブ（非公開化）の伝播。Wiki.js ページを unpublish + private にし、
        // メタデータを Archived にする（ゲートウェイの一覧・個別から不可視）。メタデータ未同期でも
        // Wiki.js 側の非公開化は正準パス（DocumentId 由来）で試みる（冪等・deny-closed）。
        if (ev.Status == "archived")
        {
            await wikiJs.ArchivePageAsync(WikiPage.PathFor(ev.DocumentId), ct);

            var archivedPage = await db.Pages
                .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ct);
            if (archivedPage is not null)
            {
                archivedPage.Archive();
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Archived document {DocumentId} on Wiki.js", ev.DocumentId);
            return;
        }

        if (ev.Status != "published" && ev.Status != "normalized") return;

        // FR-19, ADR-0046 D-01: 個人資料は Wiki.js の同期対象から外す。
        // 「private-note は WikiService の push 対象に含めない。Wiki.js 上に個人資料のページは
        // 作られない」（同 D-01）。本文編集は Obsidian 経路に限る（同 D-03）。
        //
        // **アーカイブ伝播（上の分岐）より後に置いてある。** 個人資料であってもアーカイブは通し、
        // ページが在れば非公開化する（ArchivePageAsync は冪等で deny-closed の向き）。
        //
        // ★［2026-09-05 追記 / #449］**「計画は述べていない」は解消した。** 従前ここには
        // 「doc_scope が生涯で変わり得るのかも計画は述べていない。実装で決めず計画へ問う」と
        // 書いてあったが、**計画は 2026-08-23 に答えている** —— ADR-0058 決定 1・2 が
        // **`doc_scope` を作成時に確定する属性と定め、更新経路に変更要求の拒否を課した**。
        // 実装も着地済みである（DocumentAttributes.ValidateDocScopeUnchanged / IADR-0278）。
        //
        // 🔴 **したがって「組織文書が後から個人資料へ変わる」経路は上流で塞がれている。**
        // ここで既存の Wiki.js ページを消す分岐は**足さない** —— 起こり得ない遷移のための
        // 消去は、上流が破れたときに**気づかせずに証拠を消す**方向に働く。
        // 後段の振る舞い（既存ページは残り、以後更新されない）は二層目として
        // DocumentSyncConsumerTests の該当ケースが固定し続ける。
        //
        // ADR-0046 D-01 の本則は変わらない —— 「private-note は push 対象に含めない。
        // Wiki.js 上に個人資料のページは作られない」。
        if (IsPrivateNote(ev.Attributes))
        {
            logger.LogInformation(
                "Skipped Wiki.js sync for private-note document {DocumentId} (ADR-0046 D-01)",
                ev.DocumentId);
            return;
        }

        // 1) ABAC 同期メタデータの upsert（ゲートウェイのフィルタ用・単一真実源）。
        var existing = await db.Pages
            .FirstOrDefaultAsync(p => p.DocumentId == ev.DocumentId, ct);

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
        var markdown = await contentReader.ReadAsync(ev.MarkdownUri, ev.Title, ct);
        await wikiJs.UpsertPageAsync(
            new WikiJsPage(page.WikiPath, ev.Title, markdown, ev.Tags, IsPrivate: !isPublic),
            ct);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Synced document {DocumentId} to Wiki.js at {Path}", ev.DocumentId, page.WikiPath);
    }
}
