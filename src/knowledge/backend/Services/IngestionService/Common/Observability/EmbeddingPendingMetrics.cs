using System.Diagnostics.Metrics;

namespace IngestionService.Common.Observability;

// FR-02, FR-03, NFR-06, [[IADR-0536]] 決定 3 (#1908): **埋め込み待ちの点（語彙索引に全文だけで書いた public / internal の点）の計器。**
//
// ## なぜ要るか
//
// 埋め込みが使えない間も public / internal の文書を全文だけで書くと、キーワードとハイブリッドの検索では当たるので、
// **埋め込みの不調が検索の結果からは見えにくくなる**（IADR-0497 決定 2 が避けた「覆い隠す」形）。オーナー裁定（2026-10-14）は
// これを受け入れる代わりに、待ちの件数を計器とログで読めるようにすることを求めた。**0 が正常である。**
//
// - ゲージ `ingestion.embedding_pending.points`（Prometheus では `ingestion_embedding_pending_points`）: 埋め直しの器が
//   周期ごとに数えた語彙索引の待ちの点の数。**起動してからまだ 1 度も数えていない間は系列を出さない**
//   （0 を出すと「待ちなし」と読めてしまう。不在は `absent()` で読む）。一度数えた後に周期が例外で抜けた
//   （Qdrant に届かない等）ときは**前回の値を残す**（[[IADR-0536]] §結果。その間は常駐の器の Warning を合わせて読む）。
// - 計数 `ingestion.embedding_pending.written`: 取り込みが待ちの点として書いた数（タグ `ingestion.embedding_pending_reason` = transient / rejected）。
// - 計数 `ingestion.embedding_pending.reembedded`: 埋め直して主へ移した数。
//
// 🔴 **文書 ID・題名をタグにしない**（基数が無界）。理由の値域は `EmbeddingPendingReasons` の 2 値に閉じる。
public sealed class EmbeddingPendingMetrics
{
    // Meter 名はサービス名と一致させる（`Program.cs` の `ServiceName`。OTLP の収集対象へ AddMeter する）。
    public const string MeterName = "microservices-platform.ingestion-service";

    public const string PendingGaugeName = "ingestion.embedding_pending.points";
    public const string WrittenCounterName = "ingestion.embedding_pending.written";
    public const string ReembeddedCounterName = "ingestion.embedding_pending.reembedded";
    public const string ReasonTag = "ingestion.embedding_pending_reason";

    // 「未観測」の印（件数は 0 以上なので負の値は正当な観測値と衝突しない）。
    private const long Unknown = -1;

    private long _pending = Unknown;
    private readonly Counter<long> _written;
    private readonly Counter<long> _reembedded;

    public EmbeddingPendingMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(PendingGaugeName, Observe, unit: "{point}",
            description: "語彙索引に全文だけで書いた埋め込み待ちの点の数（0 が正常。まだ数えていない間は系列を出さない）");
        _written = meter.CreateCounter<long>(WrittenCounterName, unit: "{point}",
            description: "埋め込みが使えず、語彙索引へ全文だけで書いた public / internal の点の数");
        _reembedded = meter.CreateCounter<long>(ReembeddedCounterName, unit: "{point}",
            description: "埋め込み待ちの点を埋め直し、主のコレクションへ移した数");
    }

    // 埋め直しの器が数えた値を記録する（ゲージが読む）。
    public void RecordPending(long count) => Interlocked.Exchange(ref _pending, Math.Max(0, count));

    public void RecordWritten(int count, string reason)
    {
        if (count > 0)
            _written.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));
    }

    public void RecordReembedded(int count)
    {
        if (count > 0)
            _reembedded.Add(count);
    }

    // 試験と器が読む最後の観測値（未観測は null）。
    public long? LastPending
    {
        get
        {
            var value = Interlocked.Read(ref _pending);
            return value == Unknown ? null : value;
        }
    }

    private IEnumerable<Measurement<long>> Observe() =>
        LastPending is { } value ? [new Measurement<long>(value)] : [];
}
