using IngestionService.Common.Concurrency;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace IngestionService.Features.Ingestion.RemoveDeleted;

// 🔴 FR-02, FR-06, FR-19, ADR-0027, ADR-0057, IADR-0640 決定 1〜3 (#2263): **文書の削除を受け、版の印を削除済みの墓石にする。**
//
// 削除の後に、削除の前の最後の `DocumentUpdated` が再配送されると、取り込みは取り込み直していた（版の印が削除の事実を持たなかった）。
// 検索の索引から点を消すのは Retrieval の `retrieval-delete` 段であり、本段はそれと重なって全コレクションから点を消したうえで
// 印を墓石にする。どちらも文書 ID による削除で冪等である。
//
// - 順序の門: 印より厳密に古い削除（作り直した文書の版を反映した後に遅れて届いた削除）は適用しない。等しい削除（再配送）は適用する。
//   規則は `DocumentIndexOrdering.IsStaleDelete`（グラフの `GraphSyncOrdering.IsStaleDelete` と同じ）。
// - 消してから印を書く（取り込みの撤収の門と同じ順）。
// - 取り込みの受け口と文書ごとに直列にする（`DocumentIndexLocks`）。待ちは受け口の実行期限（取り込みの受け口と同じ値）が切る。
// 失敗時: 例外を送出し、Wolverine の再試行・デッドレター（UsePlatformMessagingDefaults）へ委ねる。
public class DocumentDeletedConsumer(
    IIngestionVectorStore store,
    ConsumerCallTimeouts calls,
    IngestionTimeouts timeouts,
    DocumentIndexLocks locks,
    ILogger<DocumentDeletedConsumer> logger,
    QdrantBootstrapState bootstrap) : IPipelineStep<DocumentDeleted>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "ingestion-delete";

    public async Task Handle(DocumentDeleted ev, CancellationToken ct)
    {
        // 🔴 FR-02, [[IADR-0650]] 決定 4 (#2283): コレクションの保証の成功を（上限まで）待つ（無いと全コレクションからの削除が投げる）。
        await bootstrap.WaitForConsumerAsync(ct);
        using var _ = await locks.AcquireAsync(ev.DocumentId, ct);

        var indexed = await calls.RunAsync(StepName, IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
            t => store.GetIndexVersionAsync(ev.DocumentId, t), ct);
        if (DocumentIndexOrdering.IsStaleDelete(indexed, ev.DeletedAt, ev.Sequence))
        {
            logger.LogInformation(
                "DocumentDeleted {Id}: deleted_at={DeletedAt:O}/{Sequence} is older than the indexed version {Indexed:O}/{IndexedSequence}; "
                + "skipped (a recreated document's version is already indexed; IADR-0640)",
                ev.DocumentId, ev.DeletedAt, ev.Sequence, indexed!.UpdatedAt, indexed.Sequence);
            return;
        }

        await calls.RunAsync(StepName, IngestionTimeouts.VectorStoreTarget, timeouts.DeleteFromAll,
            t => store.DeleteByDocumentFromAllAsync(ev.DocumentId, t), ct);
        await calls.RunAsync(StepName, IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
            t => store.SetIndexVersionAsync(ev.DocumentId, DocumentIndexVersion.Tombstone(ev.DeletedAt, ev.Sequence), t), ct);
        logger.LogInformation(
            "DocumentDeleted {Id}: removed the document from every index and recorded the deletion (IADR-0640)", ev.DocumentId);
    }
}
