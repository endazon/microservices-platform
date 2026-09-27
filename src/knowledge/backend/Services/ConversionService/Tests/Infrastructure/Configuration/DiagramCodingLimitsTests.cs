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
        // #1641 / #1654 D: 本文変換の外部プロセスの期限。300 ＞ 90 ＋ 10（版の確認）＋ 10（刈り取り）＋ 120 ＋ 20 ＝ 250 で、鍵の無い稼働構成が起動する。
        limits.BodyConversionTimeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    [Trait("TestKind", "Unit")]
    public void 一未満は一秒に丸める()
    {
        var limits = DiagramCodingLimits.From(Config(new()
        {
            [DiagramCodingLimits.CallTimeoutKey] = "0",
            [DiagramCodingLimits.BudgetKey] = "-5",
            [DiagramCodingLimits.BodyConversionTimeoutKey] = "0",
            [DiagramCodingLimits.HandlerTimeoutKey] = "24",
        }));

        limits.CallTimeout.Should().Be(TimeSpan.FromSeconds(1));
        limits.Budget.Should().Be(TimeSpan.FromSeconds(1));
        limits.BodyConversionTimeout.Should().Be(TimeSpan.FromSeconds(1));
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

    // #1641: 受け口の期限が「本文変換の期限＋総枠＋1 回の期限」を超えない構成も起動を止める（境界の等号を含む）。
    // 1 行目は #1624 の検査（総枠＋1 回＝140 秒）なら通る値である —— 本文変換の期限を足さない変異（M4）はここで赤になる。
    [Theory]
    [Trait("TestKind", "Unit")]
    [InlineData("20", "120", "90", "230")]
    [InlineData("20", "120", "90", "200")]
    [InlineData("1", "2", "1", "4")]
    // #1654 D: 版の確認（10 秒）と刈り取りの上限（10 秒）も足す。次の 2 行は #1641 の式（本文変換 ＋ 総枠 ＋ 1 回）なら通る値で、
    // ちょうど新しい式の境界（等号）である —— 2 つを足さない変異（M3）はここで赤になる。
    [InlineData("20", "120", "90", "250")]
    [InlineData("1", "2", "1", "24")]
    public void 受け口の期限が本文変換と総枠と一回の期限の和を超えなければ起動を止める(
        string call, string budget, string body, string handler)
    {
        var act = () => DiagramCodingLimits.From(Config(new()
        {
            [DiagramCodingLimits.CallTimeoutKey] = call,
            [DiagramCodingLimits.BudgetKey] = budget,
            [DiagramCodingLimits.BodyConversionTimeoutKey] = body,
            [DiagramCodingLimits.HandlerTimeoutKey] = handler,
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{DiagramCodingLimits.HandlerTimeoutKey}*{DiagramCodingLimits.BodyConversionTimeoutKey}*");
    }

    // 陽性対照: 和（本文変換 90 ＋ 版の確認 10 ＋ 刈り取り 10 ＋ 総枠 120 ＋ 1 回 20 ＝ 250。#1654 D）を 1 秒でも超えれば起動する
    // （構成で本文変換の期限を与えたとき、その値が採られる）。
    [Fact]
    [Trait("TestKind", "Unit")]
    public void 受け口の期限が四つの和を超えれば起動し本文変換の期限は構成の値になる()
    {
        var limits = DiagramCodingLimits.From(Config(new()
        {
            [DiagramCodingLimits.CallTimeoutKey] = "20",
            [DiagramCodingLimits.BudgetKey] = "120",
            [DiagramCodingLimits.BodyConversionTimeoutKey] = "90",
            [DiagramCodingLimits.HandlerTimeoutKey] = "251",
        }));

        limits.BodyConversionTimeout.Should().Be(TimeSpan.FromSeconds(90));
        limits.HandlerTimeout.Should().Be(TimeSpan.FromSeconds(251));
    }

    // 前提の固定: Wolverine の受け口の実行期限の既定は 60 秒である（本件の欠陥の出所。上げ下げされたら見直す）。
    [Fact]
    [Trait("TestKind", "Unit")]
    public void Wolverine_の既定の実行期限は60秒である()
    {
        new WolverineOptions().DefaultExecutionTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    // 受け口の期限の方針は、Wolverine が受け口へ渡す ct を**実際に**その長さで取り消す（ローカルキューで配送して測る）。
    // 期限 1 秒の方針を入れ、発行から受け口の ct が立つまでの時間を測る（既定のままなら 60 秒待つ）。
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

        // 期限の起点は受信側の Executor（発行より後）なので、発行時刻から測れば下限（1 秒。タイマ分解能の分だけ 900 ms に緩める）は守られる。
        // 受信側の初回のコード生成が期限の内に入ることがあるため、受け口の中から測ると下限が揺れる（負荷下で実測）。
        var published = Stopwatch.GetTimestamp();
        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new RawDocumentFetched(
            Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/probe.docx", "storage://bucket/raw/probe.docx",
            "application/msword", new Dictionary<string, string>(), [], DateTimeOffset.UtcNow));

        var cancelledAt = await normalizer.CancelledAt.Task.WaitAsync(TimeSpan.FromSeconds(45),
            TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        // 既定（60 秒）のままなら 45 秒の待ちで落ちる。上限は負荷下の揺らぎを見込んで 30 秒に置く（既定との区別には十分）。
        Stopwatch.GetElapsedTime(published, cancelledAt).Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)).And.BeLessThan(TimeSpan.FromSeconds(30));
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

    // #1641: 本番の配線は、本文変換の外部プロセスの期限（既定 90 秒）を pandoc・pdftotext の両方の変換器へ渡す
    // （変換器は期限を必須の引数で受ける。DI の登録から外れれば解決で止まる）。
    [Fact]
    [Trait("TestKind", "Integration")]
    public async Task 本番の配線は本文変換の期限を両方の変換器へ渡す()
    {
        await using var factory = new Factory();
        var services = factory.Services;

        var expected = TimeSpan.FromSeconds(DiagramCodingLimits.DefaultBodyConversionTimeoutSeconds);
        services.GetRequiredService<PandocConversionService>().ProcessTimeout.Should().Be(expected);
        services.GetRequiredService<PdfTextLayerConverter>().ProcessTimeout.Should().Be(expected);
    }

    // 受け口の ct が立つまで待ち、立った時刻（`Stopwatch` のタイムスタンプ）を返す。
    private sealed class WaitsForCancellationNormalizer : INormalizationService
    {
        public TaskCompletionSource<long> CancelledAt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NormalizationResult> NormalizeAsync(RawDocumentFetched raw, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                CancelledAt.TrySetResult(Stopwatch.GetTimestamp());
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
