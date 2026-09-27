using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RetrievalService.Tests.Grpc;

// NFR-16 (#1637): ログの水準を観測するためのダブル（新規パッケージを増やさないため手書きする。
// `DocumentService.Tests` の同名の器と同型）。呼び出し元の取り消しが縮退へ畳まれると Error / Warning が出る ——
// 取り消しの対照は「例外が外へ出た」に加えて「縮退のログが出ていない」を測る。
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public IReadOnlyList<Entry> OfLevel(LogLevel level) => [.. _entries.Where(e => e.Level == level)];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
}
