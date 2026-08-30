using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Common.Observability;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentService.Features.Documents.Catalog;

// FR-01, UC-04: ConversionService が発行する DocumentNormalized を購読し、
// 正規化文書をカタログ（正本）へ登録する。登録後 DocumentUpdated を発行して
// 取り込み（IngestionService）・Wiki 同期（WikiService）へ連鎖させる（IADR-0001）。
public class DocumentNormalizedConsumer(
    DocumentDbContext db,
    IDocumentUpdatedPublisher bus,
    IObjectStorageClient storage,
    IngestTagMetrics metrics,
    ILogger<DocumentNormalizedConsumer> logger) : IConsumer<DocumentNormalized>, IPipelineStep
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "catalog";

    public async Task Consume(ConsumeContext<DocumentNormalized> context)
    {
        var ev = context.Message;
        var ct = context.CancellationToken;

        // FR-01: パイプライン全体で ID を一貫させ、同一イベントの再配信に対して冪等に upsert する。
        // SC-05, SC-09, #637: **辞書に無いタグは文書へ付けない。**
        // 「既定タグ辞書に整合」は**経路を問わない不変条件**である（計画確定・2026-08-09）。
        // **取り込み経路はタグを生成しない**ので、ここが非空になること自体が規定違反であり、
        // **件数を観測して検出する**（[[IADR-0153]] 決定 5 / SC-10。**0 が正常**）。
        var tags = await KnownTagsAsync(ev.Tags, ct);

        // ADR-0050 決定 1 (#911): 本文指紋。正規化本文をストレージから読んで計算する
        // （DocumentNormalized は本文を運ばず、URI は文書 ID 固定で内容に依存しないため、
        // 発行側で読む以外に「本文が変われば変わる」値を得る手段が無い）。
        // ストレージ縮退（CanResolve=false）では null = 不明（解除判定を発火させない側に倒す）。
        string? fingerprint = null;
        if (storage.CanResolve(ev.MarkdownUri))
            fingerprint = DocumentBodyIntake.Fingerprint(await storage.GetTextAsync(ev.MarkdownUri, ct));

        var doc = await db.Documents.FindAsync(new object?[] { ev.DocumentId }, ct);
        if (doc is null)
        {
            // ADR-0057 決定 1, [[IADR-0296]]: **`ev.AssetUris` を台帳へ写す。**
            // 従前はイベントが運んでいるのに渡しておらず、図表資産は台帳から辿れず
            // **削除が届かなかった**（受け入れ基準①が構造的に満たせない状態だった）。
            doc = Document.CreateNormalized(ev.DocumentId, ev.Title, ev.MarkdownUri,
                ev.Attributes, tags, fingerprint, ev.AssetUris);
            db.Documents.Add(doc);
            logger.LogInformation("Cataloged normalized document {Id} title={Title}",
                ev.DocumentId, ev.Title);
        }
        else
        {
            // **タグ欄は上書きしない**（SC-05「再正規化はタグ欄を上書きしない」）——
            // 上書きすると管理者が付けたタグが再同期のたびに消える。
            // [[IADR-0296]]: 資産 URI も再正規化の結果で差し替える（属性と同じ扱い）。
            doc.ApplyNormalized(ev.Title, ev.MarkdownUri, ev.Attributes, fingerprint, ev.AssetUris);
            logger.LogInformation("Updated cataloged document {Id} from re-normalization",
                ev.DocumentId);
        }

        await db.SaveChangesAsync(ct);

        // FR-01: カタログ登録を後続フロー（取り込み・Wiki 同期）へ通知する。
        // **［#635］イベントは表示名を運ぶ**（正本は識別子。[[IADR-0153]] 決定 2）——
        // 射影（Qdrant / Wiki.js）は人が読む面であり、下流は変わらない。
        // ADR-0027 / E3b: 発行は Wolverine（IDocumentUpdatedPublisher 経由）。本段の**購読**
        // （DocumentNormalized・MassTransit）は辺 E2 の射程であり、本 PR では動かさない。
        var names = await TagResolver.NamesAsync(db, ct);
        await bus.PublishUpdatedAsync(
            doc.Id, doc.Title, doc.Status, doc.MarkdownUri,
            doc.Attributes, TagResolver.ToNames(doc.Tags, names), doc.UpdatedAt,
            doc.ContentFingerprint, ct);
    }

    // SC-05, SC-09, SC-10, #637: 辞書に在るタグだけを返し、**無いものは件数として記録して捨てる**。
    //
    // **捨てるのは、辞書整合が経路を問わない不変条件だからである**（計画確定・2026-08-09）。
    // **黙って捨てない** —— カウンタが上がるので「計画に無い経路でタグが生まれている」ことが分かる。
    // **規定どおり（取り込みはタグを生成しない）なら `incoming` は空で、この関数は何もしない。**
    // **`internal`** にしているのは、この絞り込みを `ConsumeContext` を組み立てずに検証するためである
    // （テストハーネスに本 consumer は登録されていない。実測）。
    internal async Task<List<Guid>> KnownTagsAsync(List<string> incoming, CancellationToken ct)
    {
        if (incoming.Count == 0) return [];

        // **［#635］識別子を返す。** 正本は表示名を複写しない（[[IADR-0153]] 決定 1）。
        // `ToIdsAsync` は正規化・重複除去まで行うので、**同じタグが 2 度来ても 1 件**である
        // （[[IADR-0152]] 決定 2 の使用件数と同じ理屈——数えるのは種類であって出現回数ではない）。
        var (ids, unknownNames) = await TagResolver.ToIdsAsync(db, incoming, ct);

        foreach (var unknown in unknownNames)
        {
            metrics.RecordUnknownTag("normalized");
            logger.LogWarning(
                "取り込み経路で辞書に無いタグが現れた: {Tag}（文書へは付けない。SC-05 は経路を問わない不変条件）",
                unknown);
        }

        return ids;
    }
}
