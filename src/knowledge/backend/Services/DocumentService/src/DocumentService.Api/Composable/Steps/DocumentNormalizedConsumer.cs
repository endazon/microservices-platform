using Platform.Shared.Infrastructure.Foundation.Pipeline;
using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Observability;
using DocumentService.Api.Foundation.Persistence;
using Knowledge.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocumentService.Api.Composable.Steps;

// FR-01, UC-04: ConversionService が発行する DocumentNormalized を購読し、
// 正規化文書をカタログ（正本）へ登録する。登録後 DocumentUpdated を発行して
// 取り込み（IngestionService）・Wiki 同期（WikiService）へ連鎖させる（IADR-0001）。
public class DocumentNormalizedConsumer(
    DocumentDbContext db,
    IPublishEndpoint bus,
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

        var doc = await db.Documents.FindAsync(new object?[] { ev.DocumentId }, ct);
        if (doc is null)
        {
            doc = Document.CreateNormalized(ev.DocumentId, ev.Title, ev.MarkdownUri,
                ev.Attributes, tags);
            db.Documents.Add(doc);
            logger.LogInformation("Cataloged normalized document {Id} title={Title}",
                ev.DocumentId, ev.Title);
        }
        else
        {
            // **タグ欄は上書きしない**（SC-05「再正規化はタグ欄を上書きしない」）——
            // 上書きすると管理者が付けたタグが再同期のたびに消える。
            doc.ApplyNormalized(ev.Title, ev.MarkdownUri, ev.Attributes);
            logger.LogInformation("Updated cataloged document {Id} from re-normalization",
                ev.DocumentId);
        }

        await db.SaveChangesAsync(ct);

        // FR-01: カタログ登録を後続フロー（取り込み・Wiki 同期）へ通知する。
        await bus.Publish(new DocumentUpdated(
            doc.Id, doc.Title, doc.Status, doc.MarkdownUri,
            doc.Attributes, doc.Tags, doc.UpdatedAt), ct);
    }

    // SC-05, SC-09, SC-10, #637: 辞書に在るタグだけを返し、**無いものは件数として記録して捨てる**。
    //
    // **捨てるのは、辞書整合が経路を問わない不変条件だからである**（計画確定・2026-08-09）。
    // **黙って捨てない** —— カウンタが上がるので「計画に無い経路でタグが生まれている」ことが分かる。
    // **規定どおり（取り込みはタグを生成しない）なら `incoming` は空で、この関数は何もしない。**
    // **`internal`** にしているのは、この絞り込みを `ConsumeContext` を組み立てずに検証するためである
    // （テストハーネスに本 consumer は登録されていない。実測）。
    internal async Task<List<string>> KnownTagsAsync(List<string> incoming, CancellationToken ct)
    {
        if (incoming.Count == 0) return [];

        var known = await db.Tags
            .Where(t => incoming.Contains(t.Name))
            .Select(t => t.Name)
            .ToListAsync(ct);

        foreach (var unknown in incoming.Where(t => !known.Contains(t)))
        {
            metrics.RecordUnknownTag("normalized");
            logger.LogWarning(
                "取り込み経路で辞書に無いタグが現れた: {Tag}（文書へは付けない。SC-05 は経路を問わない不変条件）",
                unknown);
        }

        return known;
    }
}
