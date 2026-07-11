using ConversionService.Worker.Composable.Steps;
using ConversionService.Worker.Foundation.Domain;
using ConversionService.Worker.Foundation.Jobs;
using ConversionService.Worker.Foundation.Persistence;
using ConversionService.Worker.Foundation.Services;
using FluentAssertions;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConversionService.Worker.Tests;

// FR-14, ADR-0018, IADR-0028: 宣言的パイプライン構成からの MassTransit トポロジ生成。
// 登録規則（既定登録・有効/無効・fail-fast）が仕様どおりであることを検証する。
public class PipelineStepRegistrationTests
{
    private const string ConvertConsumer =
        "ConversionService.Worker.Composable.Steps.RawDocumentFetchedConsumer";

    private static PipelineOptions Options(
        bool enabled = true,
        string name = "convert",
        string consumer = ConvertConsumer,
        string input = "RawDocumentFetched")
        => new()
        {
            Steps =
            [
                new PipelineStepOptions
                {
                    Name = name,
                    Service = "conversion-service",
                    Consumer = consumer,
                    Input = input,
                    Outputs = ["DocumentNormalized"],
                    Enabled = enabled,
                },
            ],
        };

    private static ServiceProvider Build(PipelineOptions pipeline)
        => new ServiceCollection()
            .AddLogging()
            .AddSingleton<INormalizationService>(new NoopNormalizer())
            // SC-07, IADR-0043: コンシューマは変換ジョブストア（EF・状況記録）に依存する。
            .AddDbContext<ConversionJobDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .AddScoped<IConversionJobStore, EfConversionJobStore>()
            .AddMassTransitTestHarness(cfg =>
                cfg.AddPlatformPipelineStep<RawDocumentFetchedConsumer>(pipeline))
            .BuildServiceProvider(true);

    private static RawDocumentFetched SampleEvent() => new(
        Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/pipe.docx",
        "storage://bucket/raw/pipe.docx", "application/msword",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["knowledge-mgmt"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task 構成なしのとき段は既定で登録される()
    {
        // 規則1: 宣言なし（Steps 空）→ 既定登録（現行配線と等価。ローカル・テスト互換）
        await using var provider = Build(new PipelineOptions());
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetService<RawDocumentFetchedConsumer>().Should().NotBeNull();
    }

    [Fact]
    public async Task 有効な段は構成に従い登録されイベントを処理する()
    {
        // 規則6: enabled: true → 登録（購読→処理→発行が機能する）
        await using var provider = Build(Options(enabled: true));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(SampleEvent());

        (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeTrue();
        (await harness.Published.Any<DocumentNormalized>()).Should().BeTrue();

        await harness.Stop();
    }

    [Fact]
    public async Task 無効化した段は登録されず購読されない()
    {
        // 規則5: enabled: false → 購読・キューを生成しない（構成のみで段を外せる＝FR-14）
        await using var provider = Build(Options(enabled: false));
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetService<RawDocumentFetchedConsumer>().Should().BeNull();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(SampleEvent());

        (await harness.Consumed.Any<RawDocumentFetched>()).Should().BeFalse();
        (await harness.Published.Any<DocumentNormalized>()).Should().BeFalse();

        await harness.Stop();
    }

    [Fact]
    public void 構成があるのに段が未宣言なら起動失敗する()
    {
        // 規則2: 適用漏れ・名称ずれの fail-fast（誤構成対策 = 10_composability-design.md §5）
        var act = () => Build(Options(name: "other-step"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*convert*");
    }

    [Fact]
    public void consumer型の宣言が実装と不一致なら起動失敗する()
    {
        // 規則3: 段名の付け替え誤りの fail-fast
        var act = () => Build(Options(consumer: "Wrong.Namespace.WrongConsumer"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*consumer*");
    }

    [Fact]
    public void input宣言が実装の購読イベントと不一致なら起動失敗する()
    {
        // 規則4: 配線ずれの fail-fast
        var act = () => Build(Options(input: "DocumentUpdated"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*input*");
    }

    [Theory]
    [InlineData("", "RawDocumentFetched")]
    [InlineData(ConvertConsumer, "")]
    public void consumerまたはinputの宣言が空なら起動失敗する(string consumer, string input)
    {
        // 規則3・4 の補強: 宣言がある以上、照合対象の空欄は照合スキップではなく起動失敗
        // （CI 検証をすり抜けた手書き構成への二重の安全弁。PR #114 レビュー指摘対応）
        var act = () => Build(Options(consumer: consumer, input: input));
        act.Should().Throw<InvalidOperationException>().WithMessage("*空*");
    }

    [Fact]
    public void 構成セクションからパイプライン宣言をバインドできる()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pipeline:Version"] = "1",
                ["Pipeline:Steps:0:Name"] = "convert",
                ["Pipeline:Steps:0:Service"] = "conversion-service",
                ["Pipeline:Steps:0:Consumer"] = ConvertConsumer,
                ["Pipeline:Steps:0:Input"] = "RawDocumentFetched",
                ["Pipeline:Steps:0:Outputs:0"] = "DocumentNormalized",
                ["Pipeline:Steps:0:Queue"] = "convert-custom",
                ["Pipeline:Steps:0:Enabled"] = "false",
            })
            .Build();

        var pipeline = configuration.GetPlatformPipeline();

        pipeline.Version.Should().Be(1);
        var step = pipeline.FindStep("convert");
        step.Should().NotBeNull();
        step!.Queue.Should().Be("convert-custom");
        step.Enabled.Should().BeFalse();
        step.Outputs.Should().ContainSingle().Which.Should().Be("DocumentNormalized");
    }

    private sealed class NoopNormalizer : INormalizationService
    {
        public Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw,
            CancellationToken ct = default)
            => Task.FromResult(new NormalizationResult(
                DeterministicGuid.ForDocument(raw.SourceId, raw.OriginalPath),
                "storage://normalized/pipe.md", [], 0, 0));
    }
}
