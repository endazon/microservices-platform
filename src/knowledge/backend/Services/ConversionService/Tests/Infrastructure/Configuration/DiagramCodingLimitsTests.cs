using System.Diagnostics;
using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace ConversionService.Tests.Infrastructure.Configuration;

// FR-12 テスト仕様 T-49 (#1621) —— UC-06, ADR-0012, ADR-0029, IADR-0008（2026-09-27 追記）:
// 図のコード化の時間の上限（1 回の期限・1 文書の総枠・受け口の実行期限）の構成と、その配線。
//
// 🔴 **前提そのものを試験で持つ。** 受け口の ct は Wolverine の 1 通ごとの実行期限（既定 60 秒）を含み、
// それより長い 1 回の期限（従前の `HttpClient` 既定 100 秒）は縮退の枝に届かない —— #1621 の最初の修正はこの前提を
// 取り違えていた。ここでは (1) Wolverine の既定が 60 秒であること、(2) 受け口の期限の方針が実際に受け口の ct を
// 取り消すこと、(3) 本番の Program.cs が 3 つの上限を配線していること、を実物で測る。
public class DiagramCodingLimitsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    [Trait("TestKind", "Unit")]
    public void 構成が無ければ既定の上限になる()
    {
        var limits = DiagramCodingLimits.From(Config([]));

        limits.Should().Be(DiagramCodingLimits.Default);
        limits.CallTimeout.Should().Be(TimeSpan.FromSeconds(20));
        limits.Budget.Should().Be(TimeSpan.FromSeconds(120));
        limits.HandlerTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    [Trait("TestKind", "Unit")]
    public void 一未満は一秒に丸める()
    {
        var limits = DiagramCodingLimits.From(Config(new()
        {
            [DiagramCodingLimits.CallTimeoutKey] = "0",
            [DiagramCodingLimits.BudgetKey] = "-5",
            [DiagramCodingLimits.HandlerTimeoutKey] = "3",
        }));

        limits.CallTimeout.Should().Be(TimeSpan.FromSeconds(1));
        limits.Budget.Should().Be(TimeSpan.FromSeconds(1));
    }

    // 受け口の期限が「総枠＋1 回の期限」を超えない構成は起動を止める（境界: ちょうど等しいときも止める）。
    [Theory]
    [Trait("TestKind", "Unit")]
    [InlineData("20", "120", "140")]
    [InlineData("20", "120", "60")]
    public void 受け口の期限が総枠と一回の期限の和を超えなければ起動を止める(string call, string budget, string handler)
    {
        var act = () => DiagramCodingLimits.From(Config(new()
        {
            [DiagramCodingLimits.CallTimeoutKey] = call,
            [DiagramCodingLimits.BudgetKey] = budget,
            [DiagramCodingLimits.HandlerTimeoutKey] = handler,
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{DiagramCodingLimits.HandlerTimeoutKey}*");
    }

    // 前提の固定: Wolverine の受け口の実行期限の既定は 60 秒である（本件の欠陥の出所。上げ下げされたら見直す）。
    [Fact]
    [Trait("TestKind", "Unit")]
    public void Wolverine_の既定の実行期限は60秒である()
    {
        new WolverineOptions().DefaultExecutionTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    // 受け口の期限の方針は、Wolverine が受け口へ渡す ct を**実際に**その長さで取り消す（ローカルキューで配送して測る）。
    // 期限 1 秒の方針を入れ、受け口の ct が立つまでの時間を測る（既定のままなら 60 秒待つ）。
    [Fact]
    [Trait("TestKind", "Integration")]
    public async Task 受け口の期限の方針は受け口の_ct_をその長さで取り消す()
    {
        var normalizer = new WaitsForCancellationNormalizer();
        using var host = new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<RawDocumentFetchedConsumer>(new PipelineOptions());
                opts.UsePlatformMessagingDefaults();
                opts.PublishMessage<RawDocumentFetched>().ToLocalQueue("conversion-timeout-probe");
                opts.Policies.Add(new RawDocumentFetchedTimeoutPolicy(TimeSpan.FromSeconds(1)));
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddSingleton<INormalizationService>(normalizer)
                .AddSingleton<IDocumentNormalizedPublisher, RecordingDocumentNormalizedPublisher>()
                .AddDbContext<ConversionJobDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
                .AddScoped<IConversionJobStore, EfConversionJobStore>()
                .DisableAllExternalWolverineTransports())
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new RawDocumentFetched(
            Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/probe.docx", "storage://bucket/raw/probe.docx",
            "application/msword", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow));

        var elapsed = await normalizer.CancelledAfter.Task.WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500)).And.BeLessThan(TimeSpan.FromSeconds(10));
    }

    // 本番の Program.cs の配線: 受け口の実行期限は 300 秒、REST の図のコード化の名前付きクライアントの期限は 20 秒、
    // 上限の値は DI から引ける（gRPC 実装と正規化が同じ値を使う）。
    [Fact]
    [Trait("TestKind", "Integration")]
    public async Task 本番の配線は三つの上限を既定値で張る()
    {
        await using var factory = new Factory();
        var services = factory.Services;

        var chain = services.GetRequiredService<HandlerGraph>().Chains
            .Should().ContainSingle(c => c.MessageType == typeof(RawDocumentFetched)).Which;
        chain.ExecutionTimeoutInSeconds.Should().Be(DiagramCodingLimits.DefaultHandlerTimeoutSeconds);

        services.GetRequiredService<DiagramCodingLimits>().Should().Be(DiagramCodingLimits.Default);
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDiagramCoder>().Should().BeOfType<LlmGatewayDiagramCoder>();
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IDiagramCoder))
            .Timeout.Should().Be(TimeSpan.FromSeconds(DiagramCodingLimits.DefaultCallTimeoutSeconds));
    }

    // 受け口の ct が立つまで待ち、立つまでの時間を返す。
    private sealed class WaitsForCancellationNormalizer : INormalizationService
    {
        public TaskCompletionSource<TimeSpan> CancelledAfter { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default)
        {
            var started = Stopwatch.StartNew();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                CancelledAfter.TrySetResult(started.Elapsed);
                throw;
            }
            throw new UnreachableException("the delay only ends by cancellation");
        }
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.ReplaceDbContextWithInMemory<ConversionJobDbContext>(_dbName);
                services.RemoveAll<MassTransit.IBusControl>();
                services.AddMassTransitTestHarness();
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
