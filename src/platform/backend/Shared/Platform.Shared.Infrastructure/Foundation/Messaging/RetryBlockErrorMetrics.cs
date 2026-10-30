using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Messaging;

// FR-12, NFR-21, IADR-0609 決定 2, [[IADR-0656]] (#2178): **Wolverine の耐久化の書き込みのメモリ上の再試行の失敗を数える計器**
// （警報 MessagingRetryBlockErrors の元。**0 が正常**）。
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

    private readonly Counter<long> _errors;

    public RetryBlockErrorMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _errors = meterFactory.Create(MeterName).CreateCounter<long>(
            CounterName,
            unit: "{error}",
            description: "Wolverine（JasperFx の RetryBlock）がメモリ上で再試行した耐久化の操作の失敗の数（0 が正常）");
    }

    public void Record(Exception exception) =>
        _errors.Add(1, new KeyValuePair<string, object?>(ErrorTypeTag, exception.GetType().Name));

    // 1 件のログが、数える対象か。
    internal static bool Matches<TState>(LogLevel level, TState state, Exception? exception)
    {
        if (exception is null || level < LogLevel.Error) return false;
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return false;
        foreach (var pair in values)
        {
            if (pair.Key == "{OriginalFormat}")
                return string.Equals(pair.Value as string, RetryTemplate, StringComparison.Ordinal);
        }
        return false;
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

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (RetryBlockErrorMetrics.Matches(logLevel, state, exception))
                metrics.Record(exception!);
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
