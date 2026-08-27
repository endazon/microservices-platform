using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Composable.Steps;

// FR-06, FR-19, UC-03, ADR-0057 決定 1 (#1016): 文書削除イベントを受信し、検索索引
// （ベクトルストア）から当該文書のチャンク・埋め込みを削除する。
//
// ADR-0057 は完全削除（FR-06 の文書削除／FR-19 の完全削除・90 日自動物理削除）が
// 「②ベクトルストアに当該文書のチャンク・埋め込みが残っていない」ことを要求へ格上げした。
// 従前 `IVectorStore.DeleteByDocumentAsync` は実装まで在るのに**製品コードからの呼び出し元が
// 0 件**だった（#1016 実測）。本段がその呼び出し元である。
//
// 冪等性: 文書 ID による削除であり、該当 0 件でも成功する（再配信に対して冪等）。
// 失敗時: 例外を送出し、Wolverine のリトライ／デッドレター（UsePlatformMessagingDefaults）へ委ねる。
//
// ⚠️ 削除対象は本サービスが検索に使うコレクション（`Qdrant:CollectionName`）である。
// モデル別コレクション横断の削除口（`DeleteByDocumentFromAllAsync`）は IngestionService 側の
// ポートにあり、本サービスからは見えない —— 既知の限界として作業仕様書
// `20260828_issue-1016_delete-propagation.md` §限界 に記録した。
public class DocumentDeletedConsumer(
    IVectorStore store,
    ILogger<DocumentDeletedConsumer> logger) : IPipelineStep<DocumentDeleted>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "retrieval-delete";

    // ADR-0027 / #1016: Wolverine のハンドラ。
    public async Task Handle(DocumentDeleted ev, CancellationToken ct)
    {
        await store.DeleteByDocumentAsync(ev.DocumentId, ct);
        logger.LogInformation(
            "Removed chunks of deleted document {DocumentId} from the search index", ev.DocumentId);
    }
}
