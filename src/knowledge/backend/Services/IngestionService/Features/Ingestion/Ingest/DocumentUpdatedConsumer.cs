using IngestionService.Common.Concurrency;
using IngestionService.Common.Observability;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace IngestionService.Features.Ingestion.Ingest;

// FR-02, UC-04: DocumentUpdated を受信し、parse→chunk→embed→index のパイプラインで
// 文書をチャンク化し Qdrant（検索インデックス）へ登録する
//
// 🔴 ADR-0027 / E3b: **購読は Wolverine へ移した**（IPipelineStep<DocumentUpdated>・IADR-0239）。
// 発行（IngestionCompleted）は IIngestionCompletedPublisher（ポート）越しで、1 ファイル 1 トランスポートを保つ
// （［2026-10-25 / #2134］発行も Wolverine へ移った。IADR-0234 単位 E4）。
//
// 🔴 #1640: **外への呼び出し（本文の取得・埋め込み・Qdrant）はすべて呼び出しごとの期限の下で行う**（`IngestionTimeouts`・
// `ConsumerCallTimeouts`）。受け口の ct は Wolverine の実行期限を含むため、期限が無いと止まった依存先は
// 受け口の ct で「取り消し」として切られる。自分の期限の時間切れは `ConsumerTimeoutException` として投げ
// （警告ログ・計器 `messaging.consumer.timeout`）、呼び出し元の取り消しはそのまま外へ出す。
// 埋め込みはチャンクごとに総枠（`EmbeddingBudget`）を判定し、使い切ったら残りを呼ばずに時間切れとして投げる。
// ［2026-10-17 / #1746・[[IADR-0560]]］語彙索引への書き込みの輪（高機密の文書・埋め込み待ちの点）も同じ値の総枠で打ち切る
// （呼び出し先 `lexical-budget`）。
// ［#1771］IngestionCompleted の購読者は結線しない（planning#741 項目 6 の裁定・2026-10-09）。
public class DocumentUpdatedConsumer(
    IDocumentContentReader reader,
    IChunkingService chunker,
    IEmbeddingService embed,
    IIngestionVectorStore store,
    IIngestionCompletedPublisher bus,
    ConsumerCallTimeouts calls,
    IngestionTimeouts timeouts,
    TimeProvider clock,
    EmbeddingPendingMetrics pendingMetrics,
    BodyMissingMetrics bodyMissingMetrics,
    ILogger<DocumentUpdatedConsumer> logger,
    DocumentIndexLocks locks,
    QdrantBootstrapState bootstrap) : IPipelineStep<DocumentUpdated>
{
    // 🔴 FR-02, FR-06, IADR-0640 決定 3 (#2263): 削除の受け口（`ingestion-delete`）と文書ごとに直列にする（DI では単一のインスタンス）。
    // 必須の引数にする（既定値で私的な排他を作ると、削除の受け口と別の排他になって直列にならない。#2270 のレビュー）。
    private readonly DocumentIndexLocks _locks = locks;

    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "ingest";

    // #1640: 外への 1 回の呼び出しを、その呼び出し先の期限の下で行う。
    private Task<T> CallAsync<T>(string target, TimeSpan timeout,
        Func<CancellationToken, Task<T>> call, CancellationToken ct)
        => calls.RunAsync(StepName, target, timeout, call, ct);

    private Task CallAsync(string target, TimeSpan timeout,
        Func<CancellationToken, Task> call, CancellationToken ct)
        => calls.RunAsync(StepName, target, timeout, call, ct);

    private Task DeleteFromAllAsync(Guid documentId, CancellationToken ct)
        => CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.DeleteFromAll,
            t => store.DeleteByDocumentFromAllAsync(documentId, t), ct);

    // FR-02, FR-05, [[IADR-0595]] 決定 1 (#2101): 版の印の読み書き（Qdrant 1 回ずつ。期限は Qdrant の 1 回と同じ）。
    private Task<DocumentIndexVersion?> GetIndexVersionAsync(Guid documentId, CancellationToken ct)
        => CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
            t => store.GetIndexVersionAsync(documentId, t), ct);

    private Task SetIndexVersionAsync(Guid documentId, DocumentIndexVersion version, CancellationToken ct)
        => CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
            t => store.SetIndexVersionAsync(documentId, version, t), ct);

    // FR-02, [[IADR-0595]] 決定 2 (#2101): 旧版の点の削除（全コレクションを巡る。期限は全削除と同じ）。
    private Task DeleteSupersededAsync(Guid documentId, DateTimeOffset version, long? sequence,
        IReadOnlyCollection<IndexedPointRef> keep, CancellationToken ct)
        => CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.DeleteFromAll,
            t => store.DeleteSupersededAsync(documentId, version, sequence, keep, t), ct);

    private Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct)
        => CallAsync(IngestionTimeouts.EmbeddingTarget, timeouts.Embedding,
            t => embed.EmbedAsync(text, confidentiality, t), ct);

    // ADR-0027 / E3b: Wolverine のハンドラ。
    public async Task Handle(DocumentUpdated ev, CancellationToken ct)
    {
        // 🔴 FR-02, [[IADR-0650]] 決定 4 (#2283): コレクション・索引の保証の成功を（上限まで）待ってから始める。排他の外で待つ。
        await bootstrap.WaitForConsumerAsync(ct);
        // 🔴 IADR-0640 決定 3 (#2263): 門の読みから書き終えた後の読み直しまでを、同じ文書の削除と交差させない。
        using var _ = await _locks.AcquireAsync(ev.DocumentId, ct);
        await HandleLockedAsync(ev, ct);
    }

    private async Task HandleLockedAsync(DocumentUpdated ev, CancellationToken ct)
    {
        // 🔴 FR-02, FR-05, [[IADR-0595]] 決定 1 (#2101): **順序の門。** 索引へ反映済みの版（版の印）より厳密に古い更新は
        // 反映しない（skip して ack する）。到着順の逆転・デッドレターからの再投入・撤収や削除の後に遅れて届いた古い更新が、
        // 新しい版（判定軸を厳しくした版を含む）を上書きしないためである。等しい版（再配送・再発行）は反映する。
        // 判定は GraphService の `GraphDocument.Apply` と同じ「保持中より古いものは適用しない」。
        // 🔴 **撤収の門より前に置く。** 古い撤収が新しい版を消すこともしない（新しい版が正である）。
        var indexed = await GetIndexVersionAsync(ev.DocumentId, ct);
        if (DocumentIndexOrdering.IsStale(indexed, ev.UpdatedAt, ev.Sequence))
        {
            logger.LogInformation(
                "DocumentUpdated {Id}: updated_at={Incoming:O}/{IncomingSequence} is older than the indexed version {Indexed:O}/{IndexedSequence} "
                + "(deleted={Deleted}); skipped (out-of-order or replayed delivery; IADR-0595, IADR-0639, IADR-0640)",
                ev.DocumentId, ev.UpdatedAt, ev.Sequence, indexed!.UpdatedAt, indexed.Sequence, indexed.Deleted);
            return;
        }

        // 🔴 FR-19, ADR-0061 決定 1・2・4 / [[IADR-0396]] 決定 4・5 (#1184): **索引の門。**
        //
        // 露出 3 トグルのうち 1 つでも ON なら載せる。**3 つとも OFF なら載せないだけでなく、
        // 既に載っているチャンクを削除する**（決定 4。ON → OFF は索引からの削除まで及ぶ）。
        //
        // **「属性で弾く」で済ませない。** 索引に本文を残したまま消費側のフィルタで隠す形は、
        // フィルタの実装ミス 1 つで露出に変わる（`ADR-0057` 決定 1・SC-19 の
        // 「いかなる方法でも復元できません」と同じ理由）。**実体を消す。**
        //
        // 判定は `DocumentExposure.IsIndexable` —— **発行側（DocumentService の門）と同じ関数**である。
        // 生産側と消費側で述語を割ると、片方だけ改名されて静かに無効化される。
        // **露出キーを持たない組織文書は true** なので、既存経路は 1 ビットも変わらない。3 つとも `excluded` を
        // 明示した組織文書（#1879 / [[IADR-0529]]）は個人資料と同じくここで撤収される。
        //
        // 🔴 **`MarkdownUri` の判定より前に置く。** 撤収は本文の所在に依存しない ——
        // 後ろに置くと、本文を持たない資料のチャンクが消えずに残る。
        if (!DocumentExposure.IsIndexable(ev.Attributes))
        {
            await DeleteFromAllAsync(ev.DocumentId, ct);
            // [[IADR-0595]] 決定 1: 撤収した版も印に残す（消した後に届いた古い更新が、撤収前の点を書き戻さないように）。
            await SetIndexVersionAsync(ev.DocumentId, new DocumentIndexVersion(ev.UpdatedAt, "", ev.Sequence), ct);
            logger.LogInformation(
                "DocumentUpdated {Id}: exposure toggles are all off; withdrew the document from every index",
                ev.DocumentId);
            return;
        }

        // 🔴 FR-06, FR-02, 計画 ADR-0141 決定 2・3, [[IADR-0551]] 決定 2 (#1961): **アーカイブの門。**
        //
        // アーカイブした文書は検索・RAG に載せない（閲覧〔SC-03〕と SC-05 の一覧には残る。どちらも索引を引かない）。
        // 露出の門と同じく**実体を消す** —— 点を残して検索の出口で状態を見て隠す形は採らない（点は状態を運ばず、
        // 出口の絞り込みの実装ミス 1 つで露出に変わる。上の露出の門と同じ理由）。
        //
        // 判定は `DocumentArchival.IsArchived`（台帳の状態。SC-05 が「アーカイブ済み」と出す値）。`lifecycle` 属性は見ない。
        // 🔴 **本文を読む前・`MarkdownUri` の判定より前に置く。** 撤収は本文の有無に依存しない（本文の無いアーカイブ済み —— #1931 の
        // 一括の対象 —— も、本文の在るアーカイブ済みも同じに外れる）。本文を読まないので埋め込みの費用もかからない。
        // アーカイブ済みの文書へのその後の更新（タグの付け替え・再発行）も status=archived のまま届くので、この門で外れたままになる。
        if (DocumentArchival.IsArchived(ev.Status))
        {
            await DeleteFromAllAsync(ev.DocumentId, ct);
            await SetIndexVersionAsync(ev.DocumentId, new DocumentIndexVersion(ev.UpdatedAt, "", ev.Sequence), ct);
            logger.LogInformation(
                "DocumentUpdated {Id}: the document is archived; withdrew the document from every index",
                ev.DocumentId);
            return;
        }

        // FR-02 例外フロー E1: 本文の所在が無ければ取り込みをスキップ
        if (ev.MarkdownUri is null)
        {
            // [[IADR-0595]] 決定 1: 版は進める（これより古い更新を後から反映しない）。点は書き換えていないので、
            // 判定軸の指紋は**索引に残っている点のもの**（直前の印）を引き継ぐ —— 次の版の分岐（先に消すか）を誤らせない。
            await SetIndexVersionAsync(ev.DocumentId,
                new DocumentIndexVersion(ev.UpdatedAt, indexed?.AccessFingerprint ?? "", ev.Sequence), ct);
            logger.LogWarning("DocumentUpdated {Id}: MarkdownUri is null, skipping ingestion", ev.DocumentId);
            return;
        }

        // NFR-28, NFR-19 (#2131): **表題を出さない。** Information は既定で集約・保持される水準であり、表題（個人資料の題名を含む・
        // 利用者が入力した文書メタデータ）が文書 1 件ごとにログ基盤へ積もる。運用者が引く鍵は文書 ID で足りる（IADR-0541 と同じ線引き）。
        logger.LogInformation("Ingesting document {Id}", ev.DocumentId);

        // 🔴 FR-02, FR-05, ADR-0016, [[IADR-0595]] 決定 2・3 (#2101): **旧版の点をいつ消すか。**
        //
        // - **判定軸（属性辞書 ＋ 共有先）が変わった、または不明（版の印が無い）なら、先に全コレクションから消す**
        //   （fail-closed。従前どおり）。機密区分の変更（例 public→confidential）でコレクションが移っても旧コレクションに
        //   残さず、この後の本文取得・書き込みが失敗しても、**旧版の判定軸で読める点が残らない**。
        // - **判定軸が変わっていなければ、新版の点を書き終えてから旧版の点を消す**（`FinishAsync`）。
        //   本文の取得・埋め込み・書き込みが失敗して再試行を使い切っても、**文書は旧版のまま索引に残る**（消えたままにならない）。
        //
        // 消してから印を書く（印を先に書いて削除が落ちると、再試行は「判定軸は同じ」と読んで先の削除を飛ばす）。
        var access = IndexAccessFingerprint.Of(ev.Attributes, ev.SharedWith);
        if (indexed is null || !string.Equals(indexed.AccessFingerprint, access, StringComparison.Ordinal))
            await DeleteFromAllAsync(ev.DocumentId, ct);
        await SetIndexVersionAsync(ev.DocumentId, new DocumentIndexVersion(ev.UpdatedAt, access, ev.Sequence), ct);

        // [[IADR-0595]] 決定 2: この取り込みで書いた点（旧版の点の削除で残す）。
        var written = new List<IndexedPointRef>();

        // FR-05, ADR-0016: 文書の機密区分（ABAC confidentiality）を埋め込み越境判定へ渡す。
        var confidentiality = ev.Attributes.GetValueOrDefault("confidentiality");

        // 🔴 FR-02, FR-05, ADR-0127 決定 1・4, [[IADR-0497]] 決定 2 (#1746): **高機密文書は埋め込まない。**
        // confidential・restricted・未指定・未知は、埋め込みを**呼ぶ前に**分けて語彙索引へだけ書く。
        // 本文はどの埋め込みの送信先（ティア A・B を問わず）へも渡らない。判定は allow-list
        // （public / internal だけが埋め込みへ進む）。ゲートウェイの越境判定（fail-closed）は残る —— 二重の守りである。
        var lexicalOnly = LexicalIndexPolicy.IsLexicalOnly(ev.Attributes);

        // FR-02 parse: 本文（Markdown）を取得する
        string markdownUri = ev.MarkdownUri;
        string markdownText;
        try
        {
            markdownText = await CallAsync(IngestionTimeouts.ContentTarget, timeouts.ContentRead,
                t => reader.ReadAsync(markdownUri, ev.Title, t), ct);
        }
        catch (DocumentBodyMissingException missing)
        {
            // 🔴 FR-02, UC-04, [[IADR-0540]] 決定 1・3・4 (#1931): **本文のオブジェクトが無い（NoSuchKey）のは恒久的な欠落である。**
            // 再試行しても現れない（9/26 の SeaweedFS 切り替えより前の文書は、旧ストレージの中身を破棄したため本文が無い）。
            // 例外を上げると再試行の後 DLQ へ入り、DLQ を汚す。ここで**記録だけして正常に終える**（ack される）。
            //
            // ［2026-10-16 / #1931 項目 3・[[IADR-0549]]］**本文の無い文書の扱いはオーナー裁定 B（アーカイブ）で決まった。**
            // 9/26 の切り替えより前の本文の無い文書は、DocumentService の一回限りの管理操作（`POST /documents/archive-bodyless`）で
            // 既存の状態「アーカイブ」へ移す。題名だけの点は書かない（メタデータ点 `IndexMetadataOnlyAsync` も使わない。IADR-0549 決定 4）。
            // ［2026-10-16 / #1961・[[IADR-0551]]］その `DocumentUpdated`（status=archived）は、上の**アーカイブの門**で全索引から外れて
            // 終わり、**この分岐へは来ない**（本文を読まない）。ここへ来るのは、アーカイブしていない文書の本文が欠けているときだけである。
            //
            // 完了イベントは発行しない（取り込みは完了していない。購読者も無い —— #1771）。
            // ログに出すのは文書 ID・本文の所在（storage://<bucket>/<key>。秘密情報を含まない）・更新日時だけである。
            // 作成日時はイベントが運ばない（`DocumentUpdated` に項目が無い）ので、更新日時で代える。
            bodyMissingMetrics.RecordMissing();
            // [[IADR-0595]] 決定 2: 新版は点を持たない（題名だけの点も書かない）ので、旧版の点を消す（従前の「先に全消し」と同じ結果）。
            // 本文の欠落は恒久的であり、再試行で旧版を残す意味が無い。
            // 戻り値（追い越されたか）は見ない —— どちらでもこの後は記録して終えるだけで、完了イベントは出さない。
            await FinishAsync(ev, written, ct);
            logger.LogWarning(
                "Ingestion {DocumentId}: body object missing (NoSuchKey) at {BodyKey} (updated_at={UpdatedAt:O}); "
                + "skipped without retry or dead-lettering; no point written (archive body-less documents: #1931 item 3, IADR-0549)",
                ev.DocumentId, missing.MarkdownUri, ev.UpdatedAt);
            return;
        }

        // FR-02 chunk: チャンク化
        var chunks = chunker.Chunk(markdownText);

        // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1:
        // 🔴 **チャンクが 0 件になったときが「本文なし」である。** 本文（の分割結果）そのもので判定し、
        // **上流の状態名（変換側の「本文なしで完了」）には依存しない** ——
        // 依存すると、状態名の改名や別経路（直接投入・再正規化）で静かに漏れる。
        //
        // 本文由来のチャンク・埋め込みは作らない（作れない）が、**メタデータで索引へ載せる。**
        // 載せなければ利用者はその文書の存在を知る手段を持たない（ADR-0070 決定 4 / P-01）。
        WarnOnBodyPresenceDivergence(ev, chunks.Count);

        if (chunks.Count == 0)
        {
            await IndexMetadataOnlyAsync(ev, confidentiality, lexicalOnly, written, ct);
            return;
        }

        if (lexicalOnly)
        {
            await IndexLexicallyAsync(ev, chunks, markdownUri, confidentiality, written, ct);
            return;
        }

        var chunkCount = 0;
        // FR-02, NFR-06, [[IADR-0536]] 決定 1 (#1908): 埋め込みが使えなかったチャンクの位置と理由（使えていれば -1）。
        var pendingFrom = -1;
        var pendingReason = EmbeddingPendingReasons.Transient;
        // #1640: 埋め込みの総枠の起点（チャンクを回し始めた時刻）。
        var embeddingStarted = clock.GetTimestamp();

        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            // FR-02: documentId + chunkIndex から決定的なチャンク ID を導出（冪等）
            var chunkId = ChunkId.Derive(ev.DocumentId, idx);

            // 🔴 #1640: 総枠は呼び出しの**前**に判定する（超過は最後の 1 チャンクの期限までに収まる）。
            // 使い切ったら残りを呼ばずに時間切れとして投げる —— 受け口の実行期限に切られて取り消しとして落ちるより先に。
            if (clock.GetElapsedTime(embeddingStarted) >= timeouts.EmbeddingBudget)
                throw calls.BudgetExhausted(StepName, IngestionTimeouts.EmbeddingBudgetTarget,
                    timeouts.EmbeddingBudget,
                    $"document {ev.DocumentId}: {idx} of {chunks.Count} chunks embedded");

            // FR-02 embed: 埋め込み生成（LLM Gateway 経由 / ADR-0013・ADR-0016）。機密区分で送信先・コレクションが決まる。
            var embedding = await EmbedAsync(text, confidentiality, ct);

            // 🔴 FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 1 (#1908): **埋め込めなかったら、このチャンクから後ろを
            // 全文だけの点（埋め込み待ち）として語彙索引へ書く。** 一時障害（`Retryable=true`。鍵の未設定・送信先の不調）も
            // 恒久的な拒否（`Retryable=false`。経路の無効化・次元不整合）も同じに扱い、例外を投げない（DLQ へ送らない）。
            // 鍵の投入後に埋め直しの器（`EmbeddingPendingReembedder`）が主へ移し、語彙索引の点を消す。
            //
            // ［2026-10-14］従前は一時障害を例外で再試行の後 DLQ へ送り（#98）、恒久的な拒否をスキップして（[[IADR-0497]] 決定 2
            // 「埋め込めるはずの文書の不調を語彙索引で覆い隠さない」）、どちらも**点を書かなかった**。そのため鍵の無い間、
            // public / internal の文書は全文検索にも現れなかった（PoC で AST の情報収集が 0 件）。オーナー裁定（2026-10-14）で改めた。
            // 覆い隠さないために、待ちの件数を計器（`EmbeddingPendingMetrics`）と周期ごとの Warning で読めるようにしてある。
            //
            // 残りのチャンクは**埋め込みを呼ばない**（使えない送信先をチャンクの数だけ叩かない。総枠も消費しない）。
            if (!embedding.Embedded)
            {
                pendingFrom = idx;
                pendingReason = EmbeddingPendingReasons.From(embedding);
                break;
            }

            // FR-02 index: 機密区分ルーティングが決めたモデル別コレクションへ登録。chunk_index/tags、FR-05 ABAC 属性を保持
            // FR-03, SC-02, #536: 更新日時は**イベントが運んできた値をそのまま渡す**（IADR-0149 決定 5）。
            // ここで DateTimeOffset.UtcNow を採ると、再索引のたびに全文書の「更新日時」が今になる。
            // #1184: `shared_with` も同じ点へ載せる（ADR-0061 決定 5 の第 3 の判定軸）。
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertChunkAsync(embedding.Collection, chunkId, ev.DocumentId, ev.Title, text, idx,
                    embedding.Vector, markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                    ev.SharedWith, ev.Sequence, t), ct);
            written.Add(new IndexedPointRef(embedding.Collection, chunkId));
            chunkCount++;
        }

        var pendingCount = 0;
        if (pendingFrom >= 0)
        {
            for (var idx = pendingFrom; idx < chunks.Count; idx++)
            {
                // 🔴 FR-02, NFR-06, [[IADR-0560]] (#1746): 待ちの点の書き込みも総枠で打ち切る。**埋め込みの輪の始めから続けて測る**
                // （1 文書のチャンクの輪全体で 1 つの総枠。受け口の最悪の所要時間の式を変えない）。書いた分は計器に数えてから投げる。
                if (clock.GetElapsedTime(embeddingStarted) >= timeouts.EmbeddingBudget)
                {
                    if (pendingCount > 0)
                        pendingMetrics.RecordWritten(pendingCount, pendingReason);
                    throw LexicalBudgetExhausted(ev.DocumentId, idx, chunks.Count);
                }

                var chunkId = ChunkId.Derive(ev.DocumentId, idx);
                var text = chunks[idx];
                var i = idx;
                await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                    t => store.UpsertEmbeddingPendingChunkAsync(chunkId, ev.DocumentId, ev.Title, text, i,
                        markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt, ev.SharedWith, pendingReason, ev.Sequence, t), ct);
                written.Add(IndexedPointRef.Lexical(chunkId));
                pendingCount++;
            }

            pendingMetrics.RecordWritten(pendingCount, pendingReason);
            logger.LogWarning(
                "Ingestion {Id}: embedding unavailable ({Reason}; confidentiality={Confidentiality}); {Pending} of {Total} "
                + "chunk(s) written to the lexical index as full-text-only points awaiting embedding (keyword/hybrid search "
                + "finds them, semantic search does not). They are re-embedded once embedding works again (IADR-0536)",
                ev.DocumentId, pendingReason, confidentiality ?? "(unset)", pendingCount, chunks.Count);
        }

        if (!await FinishAsync(ev, written, ct))
            return;

        // FR-02: 取り込み完了イベントを発行する（ポート越し。Wolverine・非耐久。#2134）。
        // 購読者は無く、結線もしない（#1771 / planning#741 項目 6）。検索への反映は上の Qdrant 登録で成立している。
        // ［2026-10-14 / #1908］チャンク数は書いた点の数（埋めた数 ＋ 埋め込み待ちの数）である。
        await bus.PublishCompletedAsync(ev.DocumentId, chunkCount + pendingCount, DateTimeOffset.UtcNow, ct);

        logger.LogInformation("Ingestion complete for {Id}: {Count} chunks ({Pending} awaiting embedding)",
            ev.DocumentId, chunkCount + pendingCount, pendingCount);
    }

    // FR-02, FR-03, ADR-0127 決定 1・2, [[IADR-0497]] 決定 2・3 (#1746): 高機密文書の本文チャンクを語彙索引へ書く。
    // 🔴 **埋め込みを 1 回も呼ばない**ので、一時障害の分岐は通らない。
    // Qdrant への書き込みは本文チャンクと同じ期限（`VectorStore`）の下で行い、失敗は例外のまま上げて
    // ブローカの再試行へ委ねる（本文チャンクの書き込みと同じ）。
    // ［2026-10-17 / #1746・[[IADR-0560]]］**埋め込みの総枠と同じ値の総枠で打ち切る**（呼び出し先 `lexical-budget`）。
    // 従前は受け口の実行期限（既定 420 秒）だけが上から抑え、見出しの密な 1 MiB の本文（約 26 万点・実測 5 ms／点で約 1,300 秒）は
    // 期限で取り消され、4 試行の間キューを塞いでからデッドレターへ入った。判定は書き込みの**前**（超過は最後の 1 点の期限までに収まる）。
    // チャンク ID は本文チャンクと同じ規則（`ChunkId.Derive`）—— 機密区分が変わって置き場所が移ったときは、
    // 判定軸が変わっているので冒頭の全コレクション削除で古い点は消えている（[[IADR-0595]] 決定 3）。
    // 変わっていなければ、書き終えた後の旧版の削除（`FinishAsync`）が別のコレクションに残った同じ ID の点を消す。
    private async Task IndexLexicallyAsync(DocumentUpdated ev, IReadOnlyList<string> chunks,
        string markdownUri, string? confidentiality, List<IndexedPointRef> written, CancellationToken ct)
    {
        var chunkCount = 0;
        var started = clock.GetTimestamp();
        foreach (var (text, idx) in chunks.Select((t, i) => (t, i)))
        {
            if (clock.GetElapsedTime(started) >= timeouts.EmbeddingBudget)
                throw LexicalBudgetExhausted(ev.DocumentId, idx, chunks.Count);

            var chunkId = ChunkId.Derive(ev.DocumentId, idx);
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertLexicalChunkAsync(chunkId, ev.DocumentId, ev.Title, text, idx,
                    markdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt, ev.SharedWith, ev.Sequence, t), ct);
            written.Add(IndexedPointRef.Lexical(chunkId));
            chunkCount++;
        }

        logger.LogInformation(
            "Ingestion {Id}: {Count} chunk(s) indexed into the lexical index without embedding "
            + "(confidentiality={Confidentiality}; ADR-0127)", ev.DocumentId, chunkCount,
            confidentiality ?? "(unset)");

        if (!await FinishAsync(ev, written, ct))
            return;
        await bus.PublishCompletedAsync(ev.DocumentId, chunkCount, DateTimeOffset.UtcNow, ct);
    }

    // 🔴 FR-02, FR-05, [[IADR-0595]] 決定 2・4 (#2101): **新版を書き終えた後の後始末。** 旧版の点（この取り込みで書いていない、
    // 版がこの更新以下の点）を全コレクションから消す。チャンク数が減った・置き場所が移った（埋め込み待ち → 主）旧版の点が残らない。
    //
    // 書き終えた後に版の印を**読み直す**。並行してより新しい版が印を書いていたら（並行の取り込み・レプリカ）、
    // この更新は追い越されている —— 自分が書いた点ごとこの版以下の点を消し、新しい版の点（版がより新しい）だけを残す。
    // 古い版の点が新しい版の後ろに書き込まれて残る（判定軸を緩めた版が復活する）形を、取り込みの終わりで塞ぐ。
    // 追い越されたら false（完了イベントは出さない —— この版の取り込みは完了として数えない）。
    private async Task<bool> FinishAsync(DocumentUpdated ev, List<IndexedPointRef> written, CancellationToken ct)
    {
        var latest = await GetIndexVersionAsync(ev.DocumentId, ct);
        // IADR-0639 決定 3: 追い越されたかは版の印の判定と同じ規則（辞書順 (時刻, 連番)）で決める。
        // 🔴 IADR-0640 決定 2 (#2263): 門と同じ述語（`IsStale`）で判定する —— 削除の墓石と等しい鍵の版も、追い越されたものとして点を消す。
        if (DocumentIndexOrdering.IsStale(latest, ev.UpdatedAt, ev.Sequence))
        {
            await DeleteSupersededAsync(ev.DocumentId, ev.UpdatedAt, ev.Sequence, [], ct);
            logger.LogWarning(
                "DocumentUpdated {Id}: superseded while ingesting (updated_at={Incoming:O} < indexed {Indexed:O}, deleted={Deleted}); "
                + "removed the points written for the older version (IADR-0595, IADR-0640)",
                ev.DocumentId, ev.UpdatedAt, latest!.UpdatedAt, latest.Deleted);
            return false;
        }

        await DeleteSupersededAsync(ev.DocumentId, ev.UpdatedAt, ev.Sequence, written, ct);
        return true;
    }

    // FR-02, [[IADR-0560]] (#1746): 語彙索引への書き込みの総枠の使い切り（再試行せずデッドレターへ。`EmbeddingBudgetDeadLetterPolicy`）。
    private ConsumerTimeoutException LexicalBudgetExhausted(Guid documentId, int indexed, int total)
        => calls.BudgetExhausted(StepName, IngestionTimeouts.LexicalBudgetTarget, timeouts.EmbeddingBudget,
            $"document {documentId}: {indexed} of {total} chunks indexed before the lexical-index writes ran out of budget");

    // FR-02, FR-12, ADR-0070 決定 3, #1254, [[IADR-0388]] 決定 3:
    // **契約が運ぶ「本文の有無」と、ここでの判定（チャンク 0 件）が食い違ったら警告を残す。**
    //
    // 🔴 **判定そのものは変えない。** 索引は今までどおりチャンク 0 件で決める
    // （[[IADR-0358]] 決定 1。上流の状態名に依存すると、改名や別経路で静かに漏れる）。
    // ここが足すのは**観測だけ**である —— 二重化した情報のどちらかだけが変わったとき、
    // 従来は誰も気づかないまま索引の中身が割れていた。
    //
    // ⚠️ **`HasBody=true` で 0 件は「変換以外の経路で空本文が投入された」でも起きる**
    // （直接投入・空ファイル）。これは異常ではないが、**黙って本文なし扱いにするのは異常**なので
    // 同じ口で鳴らす。既定値（項目を運ばない旧発行元）も `true` なのでここに入り得る。
    private void WarnOnBodyPresenceDivergence(DocumentUpdated ev, int chunkCount)
    {
        if (ev.HasBody && chunkCount == 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: contract says the document has a body (hasBody=true) but chunking "
                + "produced 0 chunks; indexing metadata only. Either the body is empty/unreadable or "
                + "the upstream marker is stale",
                ev.DocumentId);
        }
        else if (!ev.HasBody && chunkCount > 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: contract says the document has no body (hasBody=false) but chunking "
                + "produced {Count} chunk(s); indexing them. The upstream marker and the body disagree",
                ev.DocumentId, chunkCount);
        }
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1・2・7:
    // 本文なしの文書を**メタデータ点 1 つ**で索引する。
    //
    // ベクトルは**索引テキスト（題名・タグ）から**作る —— 本文由来ではないので
    // 「本文由来の埋め込みは作らない」（決定 4）に反しない。
    // 🔴 **埋め込みの機密区分ルーティング（ADR-0016）は本文チャンクと同一に扱う。**
    // 本文が無いことを理由に送信制御を緩めない —— 題名も文書の内容である。
    private async Task IndexMetadataOnlyAsync(
        DocumentUpdated ev, string? confidentiality, bool lexicalOnly, List<IndexedPointRef> written, CancellationToken ct)
    {
        // #1253 / [[IADR-0388]] 決定 4: 題名・タグに加えて**原本の所在とデータソース名**も材料にする
        // （ADR-0070 決定 4 が名指しする「パス」「データソース」。従前は届いていなかった）。
        var indexText = MetadataIndexText.Build(ev.Title, ev.Tags, ev.OriginalPath, ev.DataSourceName);

        // 索引テキストが空（題名もタグも無い）なら載せる意味が無い。**当たりようがない点を作らない。**
        if (indexText.Length == 0)
        {
            logger.LogWarning(
                "Ingestion {Id}: no body and no metadata to index (empty title and tags); nothing indexed",
                ev.DocumentId);
            if (!await FinishAsync(ev, written, ct))
                return;
            await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
            return;
        }

        // 🔴 FR-02, ADR-0070 決定 4, ADR-0127 決定 1, [[IADR-0497]] 決定 3 (#1746): 高機密文書のメタデータ点も
        // **埋め込まずに語彙索引へ書く**（題名も文書の内容である —— 本文チャンクと同じ規則）。
        if (lexicalOnly)
        {
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertLexicalMetadataPointAsync(ChunkId.DeriveMetadata(ev.DocumentId),
                    ev.DocumentId, ev.Title, indexText, ev.MarkdownUri, ev.Attributes, ev.Tags,
                    ev.UpdatedAt, ev.SharedWith, ev.Sequence, t), ct);
            written.Add(IndexedPointRef.Lexical(ChunkId.DeriveMetadata(ev.DocumentId)));
            logger.LogInformation(
                "Ingestion {Id}: no body; indexed metadata only into the lexical index without embedding "
                + "(confidentiality={Confidentiality})", ev.DocumentId, confidentiality ?? "(unset)");
            if (!await FinishAsync(ev, written, ct))
                return;
            await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
            return;
        }

        var embedding = await EmbedAsync(indexText, confidentiality, ct);

        if (embedding.Embedded)
        {
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertMetadataPointAsync(embedding.Collection,
                    ChunkId.DeriveMetadata(ev.DocumentId), ev.DocumentId, ev.Title, indexText,
                    embedding.Vector, ev.MarkdownUri, ev.Attributes, ev.Tags, ev.UpdatedAt,
                    ev.SharedWith, ev.Sequence, t), ct);
            written.Add(new IndexedPointRef(embedding.Collection, ChunkId.DeriveMetadata(ev.DocumentId)));

            logger.LogInformation(
                "Ingestion {Id}: no body; indexed metadata only (title/tags/path/source). 0 body chunks",
                ev.DocumentId);
        }
        else
        {
            // 🔴 FR-02, NFR-06, [[IADR-0536]] 決定 1 (#1908): 本文チャンクと同じ規則 —— 埋め込めなければ（一時障害・恒久的な
            // 拒否とも）**メタデータ点を全文だけの点（埋め込み待ち）として語彙索引へ書く**。例外は投げない。
            var reason = EmbeddingPendingReasons.From(embedding);
            await CallAsync(IngestionTimeouts.VectorStoreTarget, timeouts.VectorStore,
                t => store.UpsertEmbeddingPendingMetadataPointAsync(ChunkId.DeriveMetadata(ev.DocumentId),
                    ev.DocumentId, ev.Title, indexText, ev.MarkdownUri, ev.Attributes, ev.Tags,
                    ev.UpdatedAt, ev.SharedWith, reason, ev.Sequence, t), ct);
            written.Add(IndexedPointRef.Lexical(ChunkId.DeriveMetadata(ev.DocumentId)));
            pendingMetrics.RecordWritten(1, reason);
            logger.LogWarning(
                "Ingestion {Id}: no body; embedding unavailable ({Reason}; confidentiality={Confidentiality}); metadata "
                + "point written to the lexical index as a full-text-only point awaiting embedding (IADR-0536)",
                ev.DocumentId, reason, confidentiality ?? "(unset)");
        }

        if (!await FinishAsync(ev, written, ct))
            return;

        // FR-02: **本文なしでも取り込みは完了である**（ADR-0070 決定 3。失敗として溜めない）。
        // チャンク数は 0 —— 本文由来のチャンクは 1 件も作っていない。
        await bus.PublishCompletedAsync(ev.DocumentId, 0, DateTimeOffset.UtcNow, ct);
    }
}
