using Microsoft.Extensions.Logging;

namespace Knowledge.IntegrationTests.Messaging;

// NFR, ADR-0027, #1337: **購読ホストの警告・例外をテスト側から読めるようにする器。**
//
// ■ なぜ要るのか（#1337 の実測に基づく）
//   fan-out テストの待ち合わせは「終端の副作用が起きたか」しか見ない。ところが
//   購読側で**受信した後に**落ちる形（例: 共有 DB で `Pages.Slug` 一意制約違反 → 再試行 →
//   デッドレター）は、テストからは「そもそも受信しなかった」と**1 ミリも見分けが付かない**。
//   ホスト側にはログが 1 行出ているのに、それはテストの出力へ届かない。
//
//   🔴 **この見分けが付かないことが、#1337 の切り分けを 1 ラウンド余分にした。**
//   本器はホストのログ（Warning 以上）をテスト側の器へ写し、失敗メッセージへ載せる。
//   **原因を名乗らせるためのものであって、判定は 1 ミリも変えない。**
//
// ⚠️ 例外を投げない・判定に使わない。診断が assert の失敗理由を覆い隠すと、
//   本来の失敗（fan-out が届かない）が別の失敗にすり替わる。
// 診断の宛名。**2 つのホストは同じ器へ書く**ので、どちらのログかを区別する鍵が要る。
internal static class FanOutHosts
{
    internal const string Ingestion = "ingestion";
    internal const string Wiki = "wiki";
}

internal sealed class HostFailureLog
{
    // 直近の件数だけ保つ。全件を溜めると失敗メッセージが読めなくなる。
    private const int Capacity = 20;
    private const int MessageLimit = 300;

    // 🔴 PostgreSQL の一意制約違反。**共有 DB ＋ 固定 Title の衝突がここに出る。**
    // Npgsql への依存を足さずに拾うため、SQLSTATE の綴りで見る（メッセージに必ず現れる）。
    private const string UniqueViolation = "23505";

    private readonly Lock _gate = new();
    private readonly Queue<(string Host, string Line)> _entries = new();

    internal void Record(string host, LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{level} {category}: {Shorten(message)}";
        if (exception is not null) line += $" / 例外 {Describe(exception)}";

        lock (_gate)
        {
            _entries.Enqueue((host, line));
            while (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    /// <summary>指定ホストの直近の警告・例外を 1 行にまとめる（失敗メッセージ用）。</summary>
    internal string Describe(string host)
    {
        (string Host, string Line)[] snapshot;
        lock (_gate) snapshot = [.. _entries];

        var lines = snapshot.Where(e => e.Host == host).Select(e => e.Line).ToArray();
        if (lines.Length == 0)
        {
            // 🔴 「無かった」と「採れなかった」を混ぜない。ここは前者である。
            return "（Warning 以上のログ無し＝受信後に落ちた形ではない）";
        }

        var head = lines.Any(l => l.Contains(UniqueViolation, StringComparison.Ordinal))
            ? $"🔴 一意制約違反（{UniqueViolation}）が記録されている"
                + "＝**受信はしている**。共有 DB で Slug（Title 由来）が衝突している。 "
            : "";
        return head + string.Join(" | ", lines);
    }

    private static string Shorten(string text)
        => text.Length <= MessageLimit ? text : text[..MessageLimit] + "…";

    private static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var ex = exception; ex is not null && parts.Count < 4; ex = ex.InnerException)
        {
            parts.Add($"{ex.GetType().Name}: {Shorten(ex.Message)}");
        }
        return string.Join(" ← ", parts);
    }
}

// ホストのログを <see cref="HostFailureLog"/> へ写すだけのプロバイダ。
// **本番の配線は 1 行も差し替えない** —— テスト側の器をホストの DI へ足すだけである
// （`ILoggerFactory` は DI に登録された `ILoggerProvider` をそのまま使う）。
internal sealed class HostFailureLoggerProvider(HostFailureLog log, string host) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new HostFailureLogger(log, host, categoryName);

    public void Dispose() { }

    private sealed class HostFailureLogger(HostFailureLog log, string host, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Warning 未満は拾わない（Information まで拾うと 1 回の実行で数百行になる）。
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            log.Record(host, logLevel, category, formatter(state, exception), exception);
        }
    }
}
