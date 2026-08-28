using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Tests.Testing;

// NFR (#901): ログを検査するためのダブル。新規パッケージを増やさないため手書きする。
//
// 本ライブラリでは **ログが機能そのもの**である箇所がある（LoggingDriftAlertSink は
// 「運用アラートへ流す」ことが責務で、戻り値を持たない）。そこを試験するには
// 「何レベルで何を出したか」を観測できる必要がある。
public sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries => _entries;

    public IReadOnlyList<Entry> OfLevel(LogLevel level) =>
        [.. _entries.Where(e => e.Level == level)];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // 構造化ログの各フィールド（例: ConfigDrift）は state の key/value に現れる。
        // 整形済み文字列だけを見ると、アラート抽出キーの喪失を見逃す。
        var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        _entries.Add(new Entry(logLevel, formatter(state, exception), exception, pairs));
    }
}
