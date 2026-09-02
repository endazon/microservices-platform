using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Domain.Ports;
using ConversionService.Domain;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
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

    private sealed class FakeNormalizationService(NormalizationResult result) : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw,
            CancellationToken ct = default) => Task.FromResult(result);
    }
}
