using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LlmGateway.Tests;

// #1819: ログの「行数」「レベル」「スタックの有無」を表明するための記録器。
// 抑制の試験は「何行出たか」が主題なので、出力先を差し替えて控える（文言の一部と構造化値を両方残す）。
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public sealed record Entry(
        string Category, LogLevel Level, string Message, Exception? Exception,
        IReadOnlyDictionary<string, object?> Values);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Recorder(string category, ConcurrentQueue<Entry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            sink.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception, values));
        }
    }
}
