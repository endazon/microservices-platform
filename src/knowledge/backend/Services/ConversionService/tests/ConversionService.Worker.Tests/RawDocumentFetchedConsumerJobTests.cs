using ConversionService.Worker.Composable.Steps;
using ConversionService.Worker.Foundation.Domain;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using ConversionService.Worker.Foundation.Services;
using AwesomeAssertions;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Extensions;

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
            Task.FromResult(new NormalizationResult(Guid.NewGuid(), "storage://bucket/a.md", [], 1, 0,
                [new NormalizedFigure("fig-0", true, "mermaid", "flowchart TD; A-->B;", null, null, null)]));
    }

    private sealed class FailingNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default) =>
            throw new InvalidOperationException("pandoc failed");
    }

    // retries: バスに構成する即時再試行の回数。既定 0 は「再試行を構成しない」＝ 1 回だけ消費する。
    private static ServiceProvider BuildHarness(INormalizationService normalizer, int retries = 0)
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
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<RawDocumentFetchedConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // SC-07: 本番は UsePlatformRetry（間隔つき 3 回）。試験では待ち時間を持たない
                    // 即時再試行で同じ**回数**を再現する（デッドレター標識の判定は回数だけを見る）。
                    if (retries > 0) cfg.UseMessageRetry(r => r.Immediate(retries));
                    cfg.ConfigureEndpoints(ctx);
                });
            })
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
            // FR-12, SC-07（AC-4）: 試行上限に達していない失敗にデッドレター標識は立たない。
            // 「失敗した」ことではなく「**再試行を使い切った**」ことが標識の意味である。
            job.Attempts.Should().BeLessThan(ConversionJobRetryPolicy.MaxAttempts);
            job.DeadLettered.Should().BeFalse();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consume_failure_exhausting_retries_marks_dead_lettered()
    {
        // FR-12, SC-07（AC-6/AC-7）: 自動再試行を使い切った継続失敗は <queue>_error（デッドレター）へ送られる。
        // 04_workflows/03_conversion-flow.md:65「継続失敗はデッドレターキューへ送り、管理者に通知する」。
        // 本番と同じ**試行上限**（初回 ＋ 再試行）で消費させ、最後の試行の失敗で標識が立つことを見る。
        await using var provider = BuildHarness(
            new FailingNormalizer(), retries: MassTransitExtensions.MaxAttempts - 1);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var ev = Raw(Guid.NewGuid());
            await harness.Bus.Publish(ev);

            // 再試行を使い切ると MassTransit は Fault<T> を発行する（＝これ以上再試行しない合図）。
            (await harness.Published.Any<Fault<RawDocumentFetched>>()).Should().BeTrue();

            using var scope = provider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IConversionJobStore>();
            var job = (await store.GetAsync(ev.FetchId))!;
            // AC-3: 状態値は 4 値のまま。デッドレターは failed の**内訳**である。
            job.Status.Should().Be(ConversionJobStatus.Failed);
            job.DeadLettered.Should().BeTrue();
            job.Attempts.Should().Be(ConversionJobRetryPolicy.MaxAttempts);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public void MaxAttempts_contract_constant_matches_platform_retry_policy()
    {
        // FR-12, SC-07（AC-11）: 契約が公開する試行上限（ConversionJobRetryPolicy）と、
        // 実際に再試行を行う設定（UsePlatformRetry）は同じ値でなければならない。
        // 契約プロジェクトから基盤プロジェクトを参照しない代わりに、両者の一致をここで束ねる
        // （IADR-0137 決定 3・決定 4）。**間隔を増減したらこのテストが落ちる。**
        ConversionJobRetryPolicy.MaxAttempts.Should().Be(MassTransitExtensions.MaxAttempts);
    }
}
