using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Domain.Ports;
using ConversionService.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

// FR-12, UC-06: ConversionService の変換ハンドラ ユニットテスト。
//
// ADR-0027（#441 E1）: 購読が Wolverine へ移ったため、MassTransit のテストハーネスは使えない。
// **本ファイルは `Handle(...)` を直接呼ぶ**（登録経路は通らない）。理由:
// ここで測るのは**正規化結果 → 発行口へ渡す値**の写像であって、ハンドラに届くかどうかではない。
// **「届くか」＝ 登録経路（`AddPlatformWolverineStep`）は `PipelineStepRegistrationTests` が
// 実際に Wolverine ホストを起こして測る。**直接呼びだけにすると登録が壊れても気づかないので、
// 両方を置いている（片方だけにしない）。
[Trait("TestKind", "Unit")]
public class RawDocumentFetchedConsumerTests
{
    [Fact]
    public async Task Consumer_publishes_DocumentNormalized_with_deterministic_id_and_assets()
    {
        var sourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var normalizer = new FakeNormalizationService(new NormalizationResult(
            DocumentId: DeterministicGuid.ForDocument(sourceId, "/docs/test.docx"),
            MarkdownUri: "storage://normalized/doc.md",
            AssetUris: ["storage://normalized/assets/fig-1.png"],
            DiagramsCoded: 1,
            DiagramsRetained: 1,
            // IADR-0154: 件数と図の記録は一致させる（1 件コード化・1 件が画像保持へ縮退）。
            Figures: [
                new NormalizedFigure("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null),
                new NormalizedFigure("fig-1", false, null, null,
                    "storage://normalized/assets/fig-1.png", "image/png", null),
            ]));

        var publisher = new RecordingDocumentNormalizedPublisher();
        // SC-07, IADR-0043: コンシューマは変換ジョブストア（EF・状況記録）に依存する。
        var options = new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ConversionJobDbContext(options);
        var consumer = new RawDocumentFetchedConsumer(
            normalizer, publisher, new EfConversionJobStore(db),
            NullLogger<RawDocumentFetchedConsumer>.Instance);

        await consumer.Handle(
            new RawDocumentFetched(
                Guid.NewGuid(), sourceId, "filesystem", "/docs/test.docx",
                "storage://bucket/raw/test.docx", "application/msword",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                ["knowledge-mgmt"],
                DateTimeOffset.UtcNow),
            new Envelope(),
            TestContext.Current.CancellationToken);

        var published = publisher.Calls.Should().ContainSingle().Which;
        published.DocumentId.Should().Be(DeterministicGuid.ForDocument(sourceId, "/docs/test.docx"));
        published.MarkdownUri.Should().Be("storage://normalized/doc.md");
        published.AssetUris.Should().ContainSingle().Which.Should().Contain("fig-1.png");
        published.Title.Should().Be("test");
    }

    // NFR-28, NFR-19, FR-19 (#2131): **原本の所在と、そこから作られる表題をログへ出さない。**
    // 表題は所在のファイル名から作られる（上の試験で `/docs/test.docx` → `test`）。所在を出せば表題を出すのと同じである。
    // 境界: 所在に改行・行区切り（U+2028）を含める。全水準・構造化の値まで見る。
    [Fact]
    public async Task Consumer_does_not_log_original_path_or_title_at_any_level()
    {
        const string marker = "秘密の相談";
        var sourceId = Guid.NewGuid();
        var fetchId = Guid.NewGuid();
        var path = $"/notes/私の日記\n\u2028{marker}.docx";
        var normalizer = new FakeNormalizationService(new NormalizationResult(
            DocumentId: DeterministicGuid.ForDocument(sourceId, path),
            MarkdownUri: "storage://normalized/doc.md",
            AssetUris: [],
            DiagramsCoded: 0,
            DiagramsRetained: 0,
            Figures: []));
        var publisher = new RecordingDocumentNormalizedPublisher();
        var options = new DbContextOptionsBuilder<ConversionJobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ConversionJobDbContext(options);
        var logger = new StructuredRecordingLogger<RawDocumentFetchedConsumer>();
        var consumer = new RawDocumentFetchedConsumer(normalizer, publisher, new EfConversionJobStore(db), logger);

        await consumer.Handle(
            new RawDocumentFetched(
                fetchId, sourceId, "filesystem", path,
                "storage://bucket/raw/x.docx", "application/msword",
                new Dictionary<string, string> { ["confidentiality"] = "internal" },
                [],
                DateTimeOffset.UtcNow),
            new Envelope(),
            TestContext.Current.CancellationToken);

        // 前提: 変換の経路を通り、表題は所在から作られた（印が表題に入っている）。
        publisher.Calls.Should().ContainSingle().Which.Title.Should().Contain(marker);
        logger.Entries.Should().NotContain(e => e.Carries(marker), "所在・表題をどの水準のログにも出さない");
        // 陰性対照: 行そのものは残り、鍵は取得 ID である。
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Carries(fetchId.ToString()));
    }

    private sealed class FakeNormalizationService(NormalizationResult result) : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw,
            CancellationToken ct = default) => Task.FromResult(result);
    }
}

// #2131: 書式化後の文言と構造化の値の両方を残すダブル（新規パッケージを増やさないため手書きする）。
file sealed class StructuredRecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyList<string> Values)
    {
        public bool Carries(string fragment) =>
            Message.Contains(fragment, StringComparison.Ordinal)
            || Values.Any(v => v.Contains(fragment, StringComparison.Ordinal));
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<Entry> _entries = new();

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
