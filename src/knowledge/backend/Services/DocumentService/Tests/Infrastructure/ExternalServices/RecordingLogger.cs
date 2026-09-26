using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DocumentService.Tests.Infrastructure.ExternalServices;

// FR-19, FR-20, [[IADR-0474]] (#1583): ログの文言を観測するためのダブル（新規パッケージを増やさないため手書きする）。
// 名簿の 2 つの読み口（退職の窓・同期の所有者）は**応答の写し方が同じで、失敗時の文言だけが違う**。
// どちらの読み口を使ったかは、失敗させた偽の名簿に対して出た文言でしか区別できない。
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> OfLevel(LogLevel level) => [.. _entries.Where(e => e.Level == level)];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
}
