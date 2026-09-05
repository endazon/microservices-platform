using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Dtos;
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
// ## 語の出現数（類似度候補の材料。IADR-0380 / #1244）
//
// 同じ指紋の変化を契機に、**同じ 1 回の本文読み取り**から語の出現数（TermProfileSynchronizer）を作り直す。
// 本文が取れなければ表題だけで作る（辺と違い「消える」ものが無いので縮退してよい）。指紋が変わらなくても
// 出現数の行が無い文書には表題から作る（既存文書の初回。backfill の代わり）。
//
// 失敗時: 例外を送出し、Wolverine のリトライ／デッドレター（UsePlatformMessagingDefaults）へ委ねる。
public class GraphDocumentSyncConsumer(
    GraphDbContext db,
    TimeProvider clock,
    IGraphContentReader content,
    LinkEdgeSynchronizer links,
    TermProfileSynchronizer termProfiles,
    ILogger<GraphDocumentSyncConsumer> logger) : IPipelineStep<DocumentUpdated>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "graph-sync";

    // ADR-0027 / #911: Wolverine のハンドラ。
    public async Task Handle(DocumentUpdated ev, CancellationToken ct)
    {
        // 🔴 FR-19, ADR-0061 決定 1・3・4 / [[IADR-0394]] 決定 4・5 (#1184): **グラフの門。**
        //
        // 索引には載る（横断検索 ON）が**グラフには出さない**個人資料があり得る（決定 3:
        // 用途の別は索引を分けずに属性で表す）。したがって受信しただけでノードを作ってはならない。
        // **ON → OFF は「表示しない」ではなくノードの削除まで及ぶ**（決定 4）——
        // 複製した ABAC 属性と辺を残すと、判定の実装ミス 1 つで出力に戻る。
        //
        // 判定は `DocumentExposure.IsGraphAllowed` —— 発行側・索引側と**同じクラスの同じ形の述語**。
        // **組織文書は常に true**（露出キーを持たない）なので既存の同期は 1 ビットも変わらない。
        if (!DocumentExposure.IsGraphAllowed(ev.Attributes))
        {
            await WithdrawAsync(ev.DocumentId, ct);
            return;
        }

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
        // IADR-0380: 出現数をどう扱ったか（ログ用）。body / title / kept のいずれか。
        string termProfile;
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

            // IADR-0380 (#1244): 同じ 1 回の読み取りから語の出現数を作り直す。本文が無ければ表題だけ。
            await termProfiles.UpsertAsync(
                ev.DocumentId, ev.Title, body, ev.ContentFingerprint, ev.UpdatedAt, ct);
            termProfile = body is null ? "title" : "body";
        }
        else if (!await termProfiles.ExistsAsync(ev.DocumentId, ct))
        {
            // 既存文書の初回（または指紋不明）。本文は読まない（ADR-0050 決定 3 の契機を増やさない）。
            await termProfiles.UpsertAsync(ev.DocumentId, ev.Title, null, node.BodyHash, ev.UpdatedAt, ct);
            termProfile = "title";
        }
        else
        {
            termProfile = "kept";
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Synced graph document {DocumentId} (attributes={AttributeCount} reinstated={Reinstated} "
            + "links={Links} edgesAdded={Added} edgesRemoved={Removed} termProfile={TermProfile})",
            ev.DocumentId, ev.Attributes.Count, reinstated,
            linkSync.Extracted, linkSync.Added, linkSync.Removed, termProfile);
    }

    // FR-19, ADR-0061 決定 4 / [[IADR-0394]] 決定 5 (#1184): グラフからの撤収。
    //
    // **消すのはノードと、その端点に触れる辺だけである。** 文書そのものは生きているので、
    // 却下済み AI 提案・リンク先の名前（`document_link_targets`）は**削除イベント
    // （`DocumentDeletedConsumer`）の担当**であり、ここでは触らない ——
    // 露出を戻したときに「却下したはずの提案が全部よみがえる」ことになる。
    //
    // ノードが無いノードは**そもそも不可視**である（鮮度契約 3・[[IADR-0242]] 決定 12-3）。
    // 辺も併せて消すのは、指す先の無い辺を残さないためである（`Seal` は両端が見える辺だけを
    // 返すので出力には出ないが、件数の材料として残り続ける）。冪等（該当 0 件でも成功）。
    private async Task WithdrawAsync(Guid documentId, CancellationToken ct)
    {
        var node = await db.Documents.FirstOrDefaultAsync(d => d.DocumentId == documentId, ct);
        var edges = await db.Edges
            .Where(e => e.SourceDocumentId == documentId || e.TargetDocumentId == documentId)
            .ToListAsync(ct);

        if (node is null && edges.Count == 0)
            return;

        if (node is not null) db.Documents.Remove(node);
        db.Edges.RemoveRange(edges);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Withdrew graph document {DocumentId} ({Edges} edge(s)): the graph exposure toggle is off",
            documentId, edges.Count);
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
