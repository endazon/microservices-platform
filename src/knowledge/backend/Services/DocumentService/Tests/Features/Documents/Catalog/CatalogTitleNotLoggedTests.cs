using System.Collections.Concurrent;
using AwesomeAssertions;
using DocumentService.Common.Observability;
using DocumentService.Features.Documents.Catalog;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace DocumentService.Tests.Features.Documents.Catalog;

// NFR-28, NFR-19, FR-19 (#2131): カタログの受け手（`DocumentNormalized`）は**表題をログへ出さない**。
// Information は既定で集約・保持される水準であり、表題（個人資料の題名を含む）が文書 1 件ごとに積もる。
// 書式化後の文言だけでなく構造化の値（Loki へは値も送られる）も見る。全水準を見る（Warning 以上へ移しても赤にする）。
[Trait("TestKind", "Unit")]
public sealed class CatalogTitleNotLoggedTests
{
    // 境界: 改行・制御文字・行区切り（U+2028）を含む表題。表題を出さなければログ行の偽装（NFR-28）も起こらない。
    private const string Marker = "秘密の相談";
    private const string Title = "私の日記\n2026\u2028" + Marker + "\u0007";

    [Fact]
    public async Task 新規の文書を台帳に載せても表題はどの水準のログにも出ない()
    {
        var logger = new StructuredRecordingLogger<DocumentNormalizedConsumer>();
        var docId = Guid.NewGuid();
        var dbName = $"catalog-title-log-{Guid.NewGuid():N}";
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddDbContext<DocumentDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IngestTagMetrics>()
            .AddCatalogTimeoutsForTests()
            .BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await new DocumentNormalizedConsumer(
                    sp.GetRequiredService<DocumentDbContext>(),
                    new CatalogNormalizationOrderingTests.RecordingUpdatedPublisher(),
                    new CatalogNormalizationOrderingTests.CountingStorage(),
                    sp.GetRequiredService<IngestTagMetrics>(),
                    sp.GetRequiredService<ConsumerCallTimeouts>(),
                    sp.GetRequiredService<CatalogTimeouts>(),
                    logger)
                .ConsumeAsync(
                    new DocumentNormalized(docId, Guid.NewGuid(), Title,
                        $"storage://knowledge-normalized/{docId:N}/document.md", [],
                        new Dictionary<string, string> { ["confidentiality"] = "internal" }, [],
                        DateTimeOffset.UtcNow,
                        // 表題は所在のファイル名から作られるので、所在にも同じ印を入れる（所在経由の漏れも捕まえる）。
                        OriginalPath: $"/notes/{Marker}.md"),
                    TestContext.Current.CancellationToken);
        }

        using (var scope = provider.CreateScope())
        {
            // 前提: 台帳には載った（＝台帳に載せる経路のログ行を通った）。
            scope.ServiceProvider.GetRequiredService<DocumentDbContext>().Documents
                .Should().Contain(d => d.Id == docId);
        }

        logger.Entries.Should().NotBeEmpty();
        logger.Entries.Should().NotContain(e => e.Carries(Marker), "表題（個人資料の題名を含む）をログへ出さない");
        // 陰性対照: 行そのものは残り、鍵は文書 ID である（ログを丸ごと消して緑にする実装を通さない）。
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Carries(docId.ToString()));
    }
}

// 書式化後の文言と構造化の値の両方を残すダブル（新規パッケージを増やさないため手書きする）。
file sealed class StructuredRecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyList<string> Values)
    {
        public bool Carries(string fragment) =>
            Message.Contains(fragment, StringComparison.Ordinal)
            || Values.Any(v => v.Contains(fragment, StringComparison.Ordinal));
    }

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.Select(p => p.Value?.ToString() ?? string.Empty).ToList()
            : [];
        _entries.Enqueue(new Entry(logLevel, formatter(state, exception), values));
    }
}
