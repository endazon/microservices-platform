using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// FR-12, NFR-21, IADR-0609 決定 2（Superseded by IADR-0657）, [[IADR-0656]] (#2178), [[IADR-0657]] (#2361):
// **Wolverine の耐久化の書き込みのメモリ上の再試行の失敗と、上限での破棄を数える計器**
// （警報 MessagingRetryBlockErrors・MessagingRetryBlockDiscarded の元。**どちらも 0 が正常**）。
// ［2026-10-30 / #2361］下の「窓」は、データソース同期の発行を 1 件ごとの短い Transactional Outbox にして閉じた（IADR-0657）。
// いまは発行がどれも送出表へのコミットを確かめてから成功を返すので、捨てられて起き得るのは重複と遅れである。
//
// ■ 窓（IADR-0609 の残余）: 送出表への書き込み（`StoreOutgoingAsync`）が一時的に失敗すると、Wolverine は例外を出さずに
//   JasperFx の `RetryBlock<T>` でメモリ上に再試行する。その間に呼び出し側の確定（DataSourceService の watermark など）が
//   成功し、再試行が**上限（既定 4 回・約 0.5 秒。Wolverine は変えていない）で捨てられるか**、途中でプロセスが止まると、
//   イベントが欠落する（上限の実測は IADR-0656）。気づく手段はブロックのエラーログだけだった。
// ■ 🔴 **照合はメッセージテンプレートの完全一致 ＋ 例外つき ＋ Error 以上**である（文面の部分一致はしない）。
//   カテゴリでは絞らない —— Wolverine がブロックへ渡すロガーのカテゴリは呼び出し元ごと・版ごとに変わり得るが、
//   テンプレートはブロックの型が持つ。版上げでテンプレートが変われば、実物のブロックを動かす試験（`RetryBlockErrorMetricsTests`）が赤になる。
// ■ 🔴 **ラベルは `error.type`（例外の型の短い名前）だけ**。利用者・文書 ID・メッセージの中身（ログの `{Item}`）は載せない。
// ■ 数えるのは `RetryBlock<T>` の再試行の失敗のすべてである（送出表の書き込みに限らない）。Wolverine は耐久化の操作
//   （送出・受信の表への書き込み、送出の後片付け）をこのブロックで回すので、どれもメモリ上の再試行の間は欠落の窓になる。
public sealed class RetryBlockErrorMetrics
{
    // 受け口の時間切れ・デッドレターと同じ Meter（`AddPlatformObservability` が収集対象へ入れている）。
    public const string MeterName = ConsumerTimeoutMetrics.MeterName;

    // Prometheus 側では `messaging_retry_block_errors_total` になる（`.` → `_`、カウンタは `_total` が付く）。
    public const string CounterName = "messaging.retry_block.errors";

    public const string ErrorTypeTag = "error.type";

    /// <summary>JasperFx 2.37.0 の <c>JasperFx.Blocks.RetryBlock&lt;T&gt;</c> が再試行の失敗で出すログのテンプレート。</summary>
    public const string RetryTemplate = "Error while trying to retry {Item}";

    // FR-12, #2361: **上限で捨てた**ことを数える計器（Prometheus では `messaging_retry_block_discarded_total`。ラベルなし）。
    // ブロックは上限（既定で最初の 1 回 ＋ 再試行 3 回・約 0.5 秒。Wolverine は変えていない）の後、項目を捨てて**例外なしで戻る**。
    // 捨てたことは Information・例外なしのログ 1 行にしか現れない（IADR-0656 §再試行の上限）。
    public const string DiscardedCounterName = "messaging.retry_block.discarded";

    /// <summary>JasperFx 2.37.0 の <c>RetryBlock&lt;T&gt;</c> が上限で項目を捨てるときのログのテンプレート（Information・例外なし）。</summary>
    public const string DiscardTemplate = "Discarding message {Message} after {Attempts} attempts";

    private readonly Counter<long> _errors;
    private readonly Counter<long> _discarded;

    public RetryBlockErrorMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _errors = meter.CreateCounter<long>(
            CounterName,
            unit: "{error}",
            description: "Wolverine（JasperFx の RetryBlock）がメモリ上で再試行した耐久化の操作の失敗の数（0 が正常）");
        _discarded = meter.CreateCounter<long>(
            DiscardedCounterName,
            unit: "{item}",
            description: "Wolverine（JasperFx の RetryBlock）が再試行の上限で捨てた耐久化の操作の数（0 が正常）");
    }

    public void Record(Exception exception) =>
        _errors.Add(1, new KeyValuePair<string, object?>(ErrorTypeTag, exception.GetType().Name));

    public void RecordDiscarded() => _discarded.Add(1);

    internal enum Match { None, RetryFailed, Discarded }

    // 1 件のログが、どちらの計器の対象か。テンプレートの完全一致で見る（文面の部分一致はしない）。
    internal static Match Classify<TState>(LogLevel level, TState state, Exception? exception)
    {
        if (level < LogLevel.Information) return Match.None;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return Match.None;
        string? template = null;
        foreach (var pair in values)
        {
            if (pair.Key == "{OriginalFormat}") { template = pair.Value as string; break; }
        }
        if (exception is not null && level >= LogLevel.Error && string.Equals(template, RetryTemplate, StringComparison.Ordinal))
            return Match.RetryFailed;
        if (string.Equals(template, DiscardTemplate, StringComparison.Ordinal))
            return Match.Discarded;
        return Match.None;
    }
}

/// <summary>
/// ログを書かず、<see cref="RetryBlockErrorMetrics"/> の照合に当たった記録だけを数えるロガーの提供者（[[IADR-0656]]）。
/// </summary>
public sealed class RetryBlockErrorLoggerProvider(RetryBlockErrorMetrics metrics) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CountingLogger(metrics);

    public void Dispose() { }

    private sealed class CountingLogger(RetryBlockErrorMetrics metrics) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // #2361: 捨てたログは Information なので、Information 以上を受ける（照合はテンプレートの比較 1 回）。
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            switch (RetryBlockErrorMetrics.Classify(logLevel, state, exception))
            {
                case RetryBlockErrorMetrics.Match.RetryFailed:
                    metrics.Record(exception!);
                    break;
                case RetryBlockErrorMetrics.Match.Discarded:
                    metrics.RecordDiscarded();
                    break;
            }
        }
    }
}

public static class RetryBlockErrorLoggingExtensions
{
    /// <summary>
    /// 再試行の失敗を数える提供者を足す（<c>AddPlatformLogging</c> が呼ぶ。全サービス共通）。
    /// </summary>
    public static ILoggingBuilder AddPlatformRetryBlockErrorMetrics(this ILoggingBuilder logging)
    {
        // 計器の工場を持たない器（汎用ホストを使わない ServiceCollection）でもロガーが組めるようにする。
        // `AddMetrics` は TryAdd なので、汎用ホストが既に登録した工場はそのまま使われる。
        logging.Services.AddMetrics();
        logging.Services.TryAddSingleton<RetryBlockErrorMetrics>();
        logging.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RetryBlockErrorLoggerProvider>());
        return logging;
    }
}
