using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Pipeline;


namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, FR-05, ADR-0033 決定 2 (#911): DocumentUpdated を購読し、ABAC 判定に要する文書属性を
// `graph_documents` へデノーマライズ保持する（「属性変更イベントを受けて即座に更新」）。
// ホップごと判定（AbacNodeFilter / AuthorizedNode）はこの複製に対して評価される。
//
// ## 鮮度契約（#911 の仕様。黙って出荷しない）
//
// 1. 判定は常に「最後に受信・適用した属性」に対して行う。同期照会による補正はしない。
// 2. **失敗方向は非対称である。** 属性の緩和の遅延は安全側。**厳格化の遅延は stale-allow の
//    漏えい窓**になる。これは WikiService の ABAC 同期（DocumentSyncConsumer）が既に持つのと
//    同型の受容であり、本サービスが新たに悪化させるものではない。
// 3. **属性レコードが無いノードは不可視**（fail-closed。IADR-0242 決定 12-3。GraphDocument 参照）。
// 4. **順序ガード**: `DocumentUpdated.UpdatedAt` が保持中より古いイベントは適用しない
//    （GraphDocument.TryApply。再配信・追い越しで「厳格化後に緩和が復活する」事故を塞ぐ）。冪等。
//
// ## 却下解除（ADR-0033 決定 10 / ADR-0050 決定 1・2 / #914 の発火側）
//
// 本文指紋（ContentFingerprint。ADR-0050 決定 1）が**変わったときだけ**、当該文書を端点とする
// 却下済み AI 提案の解除（AiSuggestion.TryReinstate）を試みる。
// - **UpdatedAt では判定しない**（ADR-0050 決定 2 が明示的に禁じた —— タグ・属性だけの更新で
//   却下が解除され、同じ提案が再び現れる）。
// - 指紋 null（発行側が指紋化できなかった＝不明）では解除を試みない（誤発火させない側に倒す）。
// - 判定の実体は AiSuggestion.TryReinstate（却下時に控えた両端の指紋と現在の指紋の比較）。
//
// ## リンク抽出と辺の差分更新（ADR-0033 決定 3・4・6・8 / #912）
//
// 同じ指紋の変化を契機に、正規化 Markdown 本文からリンクを抽出し、**当該文書を起点とする自動抽出
// の辺だけ**を差分更新する（実体は LinkEdgeSynchronizer。規則の正本は IADR-0281）。
// - **契機は却下解除と同じ「指紋の変化」である**（ADR-0050 決定 3）。属性・タグだけの更新で本文を
//   取りに行くと、storage への読み取りが更新のたびに走り、辺は 1 本も変わらない。
// - **本文が取れないときは辺を一切触らない**（IGraphContentReader が null を返す）。プレースホルダー
//   本文で抽出すると「全リンクが消えた」と解釈され、既存の自動抽出の辺が全消しになる。
//
// 失敗時: 例外を送出し、Wolverine のリトライ／デッドレター（UsePlatformMessagingDefaults）へ委ねる。
public class GraphDocumentSyncConsumer(
    GraphDbContext db,
    TimeProvider clock,
    IGraphContentReader content,
    LinkEdgeSynchronizer links,
    ILogger<GraphDocumentSyncConsumer> logger) : IPipelineStep<DocumentUpdated>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "graph-sync";

    // ADR-0027 / #911: Wolverine のハンドラ。
    public async Task Handle(DocumentUpdated ev, CancellationToken ct)
    {
        var node = await db.Documents.FirstOrDefaultAsync(d => d.DocumentId == ev.DocumentId, ct);

        string? previousHash;
        if (node is null)
        {
            previousHash = null;
            node = GraphDocument.Create(
                ev.DocumentId, ev.Title, ev.Attributes, ev.ContentFingerprint, ev.UpdatedAt);
            db.Documents.Add(node);
        }
        else
        {
            previousHash = node.BodyHash;
            if (!node.TryApply(ev.Title, ev.Attributes, ev.ContentFingerprint, ev.UpdatedAt))
            {
                // 順序ガード: 追い越し・再配信の古いイベント。何も変えずに正常終了（冪等）。
                logger.LogInformation(
                    "Skipped stale DocumentUpdated for {DocumentId} (event={EventAt:o} held={HeldAt:o})",
                    ev.DocumentId, ev.UpdatedAt, node.UpdatedAt);
                return;
            }
        }

        // ADR-0033 決定 10: 本文が変更された時点で却下を解除し、再提案を許す。
        // 変更の判定は指紋の変化**のみ**（ADR-0050 決定 2）。
        var reinstated = 0;
        var linkSync = default(LinkEdgeSynchronizer.SyncResult);
        if (ev.ContentFingerprint is not null
            && !string.Equals(previousHash, ev.ContentFingerprint, StringComparison.Ordinal))
        {
            reinstated = await ReinstateRejectedAsync(ev.DocumentId, ev.ContentFingerprint, ct);

            // ADR-0033 決定 6 / #912: 本文が変わったときだけ辺を作り直す（ADR-0050 決定 3）。
            var body = await content.ReadAsync(ev.MarkdownUri, ct);
            if (body is null)
            {
                // 🔴 **辺を触らずに抜ける。** 縮退本文で抽出すると当該文書起点の辺が全消しになる。
                logger.LogWarning(
                    "Skipped link extraction for {DocumentId}: body unavailable ({Uri})",
                    ev.DocumentId, ev.MarkdownUri ?? "(null)");
            }
            else
            {
                linkSync = await links.SyncAsync(ev.DocumentId, body, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Synced graph document {DocumentId} (attributes={AttributeCount} reinstated={Reinstated} "
            + "links={Links} edgesAdded={Added} edgesRemoved={Removed})",
            ev.DocumentId, ev.Attributes.Count, reinstated,
            linkSync.Extracted, linkSync.Added, linkSync.Removed);
    }

    // 当該文書を端点とする却下済み提案について、**現在の**両端指紋で解除判定を行う。
    // 両端の指紋は graph_documents の複製から引く（正本への同期照会はしない —— 鮮度契約 1）。
    private async Task<int> ReinstateRejectedAsync(
        Guid documentId, string currentFingerprint, CancellationToken ct)
    {
        var rejected = await db.AiSuggestions
            .Where(s => s.State == SuggestionState.Rejected
                && (s.SourceDocumentId == documentId || s.TargetDocumentId == documentId))
            .ToListAsync(ct);
        if (rejected.Count == 0) return 0;

        var endpointIds = rejected.Select(s => s.SourceDocumentId)
            .Concat(rejected.Where(s => s.TargetDocumentId is not null)
                .Select(s => s.TargetDocumentId!.Value))
            .Distinct()
            .ToList();
        var hashes = await db.Documents
            .Where(d => endpointIds.Contains(d.DocumentId))
            .ToDictionaryAsync(d => d.DocumentId, d => d.BodyHash, ct);
        // 🔴 当該文書の指紋は**イベントが運んだ新しい値**で上書きする —— 保存前のため、
        // クエリはまだ古い値（または未保存の新規行の欠落）を返す。
        hashes[documentId] = currentFingerprint;

        var now = clock.GetUtcNow();
        var count = 0;
        foreach (var s in rejected)
        {
            var sourceFp = hashes.GetValueOrDefault(s.SourceDocumentId);
            var targetFp = s.TargetDocumentId is { } target ? hashes.GetValueOrDefault(target) : null;
            if (s.TryReinstate(sourceFp, targetFp, now)) count++;
        }
        return count;
    }
}
