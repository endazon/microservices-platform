using ConversionService.Worker.Composable.Steps;
using ConversionService.Worker.Foundation.Domain;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using ConversionService.Worker.Foundation.Services;
using FluentAssertions;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Worker.Tests;

// FR-12, UC-06, SC-07, IADR-0042: 変換コンシューマが成功／失敗を IConversionJobStore に記録すること、
// 失敗時も例外を再送出して MassTransit の再試行→デッドレターを保つことを検証する。
public class RawDocumentFetchedConsumerJobTests
{
    private static RawDocumentFetched Raw(Guid id) =>
        new(id, Guid.NewGuid(), "filesystem", "/docs/a.docx", $"storage://{id}/raw",
            "application/pdf", new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["hr"],
            DateTimeOffset.UtcNow);

    private sealed class SucceedingNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            Task.FromResult(new NormalizationResult(Guid.NewGuid(), "storage://bucket/a.md", [], 1, 0));
    }

    private sealed class FailingNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            throw new InvalidOperationException("pandoc failed");
    }

    private static ServiceProvider BuildHarness(INormalizationService normalizer)
    {
        // IADR-0043: EF ストア（scoped）＋ EF InMemory DbContext。InMemory DB は provider 内で共有され、
        // コンシューマのスコープが書き込んだジョブを別スコープ（検証）から参照できる。
        // DB 名はコンテキスト生成の都度ではなく一度だけ確定させる（ラムダ内で採番すると別スコープと共有されない）。
        var dbName = Guid.NewGuid().ToString();
        return new ServiceCollection()
            .AddLogging()
            .AddDbContext<ConversionJobDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddScoped<IConversionJobStore, EfConversionJobStore>()
            .AddSingleton(normalizer)
            .AddMassTransitTestHarness(x => x.AddConsumer<RawDocumentFetchedConsumer>())
            .BuildServiceProvider(true);
    }

    [Fact]
    public async Task Consume_success_records_succeeded_job()
    {
        await using var provider = BuildHarness(new SucceedingNormalizer());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var ev = Raw(Guid.NewGuid());
            await harness.Bus.Publish(ev);

            (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
            using var scope = provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            (await store.GetAsync(ev.FetchId))!.Status.Should().Be(ConversionJobStatus.Succeeded);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consume_failure_records_failed_job_and_rethrows()
    {
        await using var provider = BuildHarness(new FailingNormalizer());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var ev = Raw(Guid.NewGuid());
            await harness.Bus.Publish(ev);

            // 変換は消費されるが失敗（例外は再送出される）。ストアに失敗が記録される。
            (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
            using var scope = provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            var job = (await store.GetAsync(ev.FetchId))!;
            job.Status.Should().Be(ConversionJobStatus.Failed);
            job.Error.Should().Contain("pandoc failed");
        }
        finally
        {
            await harness.Stop();
        }
    }
}
