using IngestionService.Common.Observability;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Ingestion.Ingest;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IngestionService.Features.Ingestion.ReembedPending;

// FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 2・3 (#1908): **埋め込み待ちの点を埋め直し、主のコレクションへ移す（1 周期分）。**
//
// 取り込みは、埋め込みが使えなかった public / internal のチャンクを語彙索引へ全文だけで書く（印 `embedding_pending`）。
// 鍵の投入などで埋め込みが使えるようになったら、ここで 1 点ずつ埋め、**主へ同じ ID・同じペイロードで書いてから、語彙索引の点を消す。**
//
// 🔴 **順序は「主へ書く → 語彙索引から消す」**。途中で落ちても点は両方に残るだけで（検索の RRF は同じチャンク ID を 1 件に束ねる）、
// 次の周期で同じ点を書き直して消す —— 収束する。逆順（消してから書く）だと、書く前に落ちた点がどこにも無くなる。
// 🔴 **書く直前に点を読み直す。** 取り込みが同じ文書を書き直していたら（点が消えた・更新日時か本文が変わった）その点は捨てる
// （古い本文を主へ書かない）。消すのは**印のある点だけ**（ストアの条件つき削除）。
// 🔴 **高機密の点は埋めない**（二重の守り）。印は public / internal にしか付かないが、属性を `LexicalIndexPolicy` で判定し直し、
// 高機密なら埋め込みを呼ばずに残す（件数に残り続けるので Warning で見える）。
// **最初の埋め込みの失敗で周期を打ち切る**（鍵が無い間、待ちの点の数だけゲートウェイを叩かない）。
public sealed class EmbeddingPendingReembedder(
    IIngestionVectorStore store,
    IEmbeddingService embed,
    EmbeddingPendingMetrics metrics,
    EmbeddingPendingOptions options,
    IngestionTimeouts timeouts,
    ILogger<EmbeddingPendingReembedder> logger)
{
    private bool _indexEnsured;

    public async Task<EmbeddingPendingSweep> SweepAsync(CancellationToken ct)
    {
        await EnsureIndexOnceAsync(ct);

        var pending = await WithTimeoutAsync(timeouts.VectorStore, store.CountEmbeddingPendingAsync, ct);
        metrics.RecordPending(pending);
        if (pending == 0)
            return new EmbeddingPendingSweep(0, 0, 0, null);

        var points = await WithTimeoutAsync(timeouts.VectorStore,
            t => store.ListEmbeddingPendingAsync(options.BatchSize, t), ct);

        var reembedded = 0;
        var heldBack = 0;
        string? stoppedBy = null;
        foreach (var point in points)
        {
            if (LexicalIndexPolicy.IsLexicalOnly(point.Attributes))
            {
                heldBack++;
                continue;
            }

            var confidentiality = point.Attributes.GetValueOrDefault(ConfidentialityLevels.AttributeKey);
            var embedding = await WithTimeoutAsync(timeouts.Embedding,
                t => embed.EmbedAsync(point.Text, confidentiality, t), ct);
            if (!embedding.Embedded)
            {
                stoppedBy = EmbeddingPendingReasons.From(embedding);
                break;
            }

            var current = await WithTimeoutAsync(timeouts.VectorStore,
                t => store.GetEmbeddingPendingAsync(point.PointId, t), ct);
            if (current is null || current.UpdatedAt != point.UpdatedAt || current.Text != point.Text)
                continue;

            await WithTimeoutAsync(timeouts.VectorStore, t => WriteToPrimaryAsync(current, embedding, t), ct);
            await WithTimeoutAsync(timeouts.VectorStore, t => store.DeleteEmbeddingPendingAsync(point.PointId, t), ct);
            reembedded++;
        }

        metrics.RecordReembedded(reembedded);
        // 🔴 残りは**数え直す**（周期の始めの件数からの引き算にしない）。周期の途中で取り込みが新しく書いた待ちの点も、
        // ゲージと Warning の件数に含めるためである（#1924 のレビュー）。
        var remaining = await WithTimeoutAsync(timeouts.VectorStore, store.CountEmbeddingPendingAsync, ct);
        metrics.RecordPending(remaining);

        if (remaining > 0)
        {
            // 🔴 **0 でない間は周期ごとに必ず 1 行出す**（覆い隠さない。[[IADR-0536]] 決定 3）。
            logger.LogWarning(
                "{Remaining} point(s) in the lexical index are awaiting embedding (full-text only: keyword/hybrid search "
                + "finds them, semantic search does not). Re-embedded {Reembedded} this cycle; stopped by {StoppedBy}; "
                + "held back as high-confidentiality {HeldBack}. Check the embedding provider (e.g. the Voyage API key) "
                + "— see docs/operations/voyage-embedding-key-runbook.md (IADR-0536)",
                remaining, reembedded, stoppedBy ?? "(none)", heldBack);
        }
        else
        {
            logger.LogInformation(
                "Re-embedded {Reembedded} point(s) awaiting embedding and moved them to the vector collection; "
                + "none remain in the lexical index (IADR-0536)", reembedded);
        }

        return new EmbeddingPendingSweep(pending, reembedded, heldBack, stoppedBy);
    }

    // 主へ書く（チャンクとメタデータ点で口を分ける。ペイロードは書いたときの値をそのまま運ぶ）。
    private Task WriteToPrimaryAsync(EmbeddingPendingPoint p, EmbeddingResult embedding, CancellationToken ct) =>
        p.IsMetadataPoint
            ? store.UpsertMetadataPointAsync(embedding.Collection, p.PointId, p.DocumentId, p.Title, p.Text,
                embedding.Vector, p.MarkdownUri, p.Attributes, p.Tags, p.UpdatedAt, p.SharedWith, ct)
            : store.UpsertChunkAsync(embedding.Collection, p.PointId, p.DocumentId, p.Title, p.Text, p.ChunkIndex,
                embedding.Vector, p.MarkdownUri, p.Attributes, p.Tags, p.UpdatedAt, p.SharedWith, ct);

    // 印の索引は最適化なので、失敗しても周期を止めない（次の周期でもう一度張る）。
    private async Task EnsureIndexOnceAsync(CancellationToken ct)
    {
        if (_indexEnsured) return;
        try
        {
            await WithTimeoutAsync(timeouts.VectorStore, async t =>
            {
                await store.EnsureEmbeddingPendingIndexAsync(t);
                return true;
            }, ct);
            _indexEnsured = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Failed to ensure the payload index on embedding_pending in the lexical index; retrying next cycle");
        }
    }

    // 外への 1 回の呼び出しに期限を掛ける（止まった依存先で周期が止まらないように）。
    // 期限切れは `TimeoutException` として上げる（器が Warning にして次の周期へ進む）。呼び出し元の取り消しはそのまま上げる。
    private static async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> call,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await call(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Embedding-pending re-embed call timed out after {timeout.TotalSeconds:0.#}s");
        }
    }

    private static async Task WithTimeoutAsync(TimeSpan timeout, Func<CancellationToken, Task> call,
        CancellationToken ct) =>
        await WithTimeoutAsync(timeout, async t =>
        {
            await call(t);
            return true;
        }, ct);
}

// [[IADR-0536]] 決定 2: 1 周期の結果（試験と器のログが読む）。
// `Pending` は周期の始めに数えた件数、`StoppedBy` は打ち切った理由（`EmbeddingPendingReasons`。打ち切らなければ null）。
public sealed record EmbeddingPendingSweep(long Pending, int Reembedded, int HeldBack, string? StoppedBy);

// FR-02, [[IADR-0536]] 決定 2 (#1908): 埋め直しの周期と 1 周期の上限。
// 既定 300 秒・64 点（鍵が無い間は 1 周期に埋め込み 1 回・数える 1 回で終わる。鍵が入れば 1 時間で 768 点）。
public sealed record EmbeddingPendingOptions(TimeSpan Interval, int BatchSize)
{
    public const string IntervalKey = "Ingestion:EmbeddingPending:IntervalSeconds";
    public const string BatchSizeKey = "Ingestion:EmbeddingPending:BatchSize";
    public const int DefaultIntervalSeconds = 300;
    public const int DefaultBatchSize = 64;

    public static EmbeddingPendingOptions Default { get; } =
        new(TimeSpan.FromSeconds(DefaultIntervalSeconds), DefaultBatchSize);

    public static EmbeddingPendingOptions From(IConfiguration configuration)
    {
        // 正の整数でなければ既定へ倒す（0 や負で周期が詰まり、ゲートウェイを叩き続けないように）。
        var seconds = PositiveOr(configuration[IntervalKey], DefaultIntervalSeconds);
        var batch = PositiveOr(configuration[BatchSizeKey], DefaultBatchSize);
        return new EmbeddingPendingOptions(TimeSpan.FromSeconds(seconds), batch);
    }

    private static int PositiveOr(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;
}
