using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Domain;
using ConversionService.Infrastructure.Persistence;
using ConversionService.Domain.Ports;
using AwesomeAssertions;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

// FR-14, ADR-0018, IADR-0028: 宣言的パイプライン構成からのトポロジ生成。
// 登録規則（既定登録・有効/無効・fail-fast）が仕様どおりであることを検証する。
//
// 🔴 ADR-0027 / #441 E1: **本ファイルは実際に Wolverine ホストを起こす。**
// ここで測るのは「登録経路（`AddPlatformWolverineStep`）が仕様どおり働くか」であり、
// ハンドラを直接呼ぶ形にすると**登録経路そのものが壊れても全部緑のまま**になる
// （W2 で作った経路が本当に使われていることを確かめる価値がある）。
// 外部トランスポートは `DisableAllExternalWolverineTransports()` で落とすので、
// ブローカは要らない —— 配送は `InvokeAsync`（プロセス内呼び出し）で駆動する。
[Trait("TestKind", "Integration")]
public class PipelineStepRegistrationTests
{
    private const string ConvertConsumer =
        "ConversionService.Features.ConversionJobs.Normalize.RawDocumentFetchedConsumer";

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

    private static IHost Build(PipelineOptions pipeline)
        => new HostBuilder()
            .UseWolverine(opts =>
            {
                opts.AddPlatformWolverineStep<RawDocumentFetchedConsumer>(pipeline);

                // 🔴 **規約探索を意図的に「効いている」状態にする。器を甘くしないため。**
                // Wolverine は明示登録とは独立にアセンブリを走査してハンドラを見つける。
                // **走査対象の決まり方は環境に依存する** —— 実測では同じコードが Windows では
                // 段の型を拾わず、Linux の CI では拾った。その結果 `enabled:false` の試験が
                // **手元だけ緑**になり、CI で落ちた（#441 E1）。
                //
                // ここで明示的にアセンブリを足すと、**どの環境でも「規約探索が段の型を見つけ得る」
                // 側に固定できる**。無効化が効くことを、いちばん厳しい条件で測る。
                opts.Discovery.IncludeAssembly(typeof(RawDocumentFetchedConsumer).Assembly);

                // 🔴 **本番（Program.cs）と同じ既定を必ず通す。**
                // ここを省いた最初の版は、サービスロケーションを許さない Wolverine の既定のまま走り、
                // EF の `AddDbContext`（不透明なラムダ Factory）に依存する段のコード生成が
                // `InvalidServiceLocationException` で落ちた。本番はヘルパの手順 5 でこれを許可済みなので、
                // **落ちたのはテストの器だけ**である —— つまり器が本番から乖離すると、
                // 本番に無い失敗を作り、本番に在る失敗を見逃す。器は本番の構成をなぞること。
                opts.UsePlatformMessagingDefaults();
            })
            .ConfigureServices(services => services
                .AddLogging()
                .AddSingleton<INormalizationService>(new NoopNormalizer())
                .AddSingleton<RecordingDocumentNormalizedPublisher>()
                .AddSingleton<IDocumentNormalizedPublisher>(sp =>
                    sp.GetRequiredService<RecordingDocumentNormalizedPublisher>())
                // SC-07, IADR-0043: 段は変換ジョブストア（EF・状況記録）に依存する。
                .AddDbContext<ConversionJobDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()))
                .AddScoped<IConversionJobStore, EfConversionJobStore>()
                // 実ブローカ接続を避ける（Wolverine が公式に用意している試験用の口）。
                .DisableAllExternalWolverineTransports())
            .Build();

    private static async Task<(IHost Host, RecordingDocumentNormalizedPublisher Publisher)> StartAsync(
        PipelineOptions pipeline)
    {
        var host = Build(pipeline);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return (host, host.Services.GetRequiredService<RecordingDocumentNormalizedPublisher>());
    }

    private static RawDocumentFetched SampleEvent() => new(
        Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/pipe.docx",
        "storage://bucket/raw/pipe.docx", "application/msword",
        new Dictionary<string, string> { ["confidentiality"] = "internal" },
        ["knowledge-mgmt"], DateTimeOffset.UtcNow);

    [Fact]
    public async Task 構成なしのとき段は既定で登録される()
    {
        // 規則1: 宣言なし（Steps 空）→ 既定登録（現行配線と等価。ローカル・テスト互換）
        var (host, publisher) = await StartAsync(new PipelineOptions());
        using var _ = host;

        await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(SampleEvent(), TestContext.Current.CancellationToken);

        publisher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task 有効な段は構成に従い登録されイベントを処理する()
    {
        // 規則8: enabled: true → 登録（購読→処理→発行が機能する）
        var (host, publisher) = await StartAsync(Options(enabled: true));
        using var _ = host;

        await host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(SampleEvent(), TestContext.Current.CancellationToken);

        publisher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task 無効化した段は登録されず購読されない()
    {
        // 規則8: enabled: false → 登録しない（購読・キューを生成しない＝構成のみで段を外せる＝FR-14）
        //
        // 🔴 **「IncludeType を呼ばない」だけでは足りない。** 規約探索が独立に段の型を拾うので、
        // ヘルパは明示的に除外する（`CustomizeHandlerDiscovery` の Excludes）。
        // 本テストは規約探索を効かせた状態で、それでも購読が生えないことを見る。
        var (host, publisher) = await StartAsync(Options(enabled: false));
        using var _ = host;

        // ハンドラが登録されていないので、プロセス内呼び出しは「宛先なし」で失敗する。
        // **握り潰して「発行が無い」だけを見ると、ハンドラが在って何もしない実装と区別できない。**
        var act = () => host.Services.GetRequiredService<IMessageBus>()
            .InvokeAsync(SampleEvent(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<Exception>();

        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public void 構成があるのに段が未宣言なら起動失敗する()
    {
        // 規則2: 適用漏れ・名称ずれの fail-fast（誤構成対策 = 10_composability-design.md §5）
        ShouldFailToBuild(Options(name: "other-step"), "convert");
    }

    [Fact]
    public void consumer型の宣言が実装と不一致なら起動失敗する()
    {
        // 規則3: 段名の付け替え誤りの fail-fast
        ShouldFailToBuild(Options(consumer: "Wrong.Namespace.WrongConsumer"), "consumer");
    }

    [Fact]
    public void input宣言が実装の購読イベントと不一致なら起動失敗する()
    {
        // 規則7: 配線ずれの fail-fast
        ShouldFailToBuild(Options(input: "DocumentUpdated"), "input");
    }

    [Theory]
    [InlineData("", "RawDocumentFetched")]
    [InlineData(ConvertConsumer, "")]
    public void consumerまたはinputの宣言が空なら起動失敗する(string consumer, string input)
    {
        // 規則3・4 の補強: 宣言がある以上、照合対象の空欄は照合スキップではなく起動失敗
        // （CI 検証をすり抜けた手書き構成への二重の安全弁。PR #114 レビュー指摘対応）
        ShouldFailToBuild(Options(consumer: consumer, input: input), "空");
    }

    // Wolverine はオプション構成中の例外を包むことがあるため、内側まで辿って本文を照合する。
    // **「何か落ちた」で通すと、別の理由で落ちても緑に見える。**
    private static void ShouldFailToBuild(PipelineOptions pipeline, string expectedFragment)
    {
        var act = () => Build(pipeline).Dispose();

        var thrown = act.Should().Throw<Exception>().Which;
        var chain = new List<Exception>();
        for (Exception? e = thrown; e is not null; e = e.InnerException) chain.Add(e);

        chain.Should().Contain(
            e => e is InvalidOperationException
                && e.Message.Contains(expectedFragment, StringComparison.Ordinal),
            "登録規則の違反は InvalidOperationException で、理由を本文に持って報告されるべきである"
            + $"（期待する語: '{expectedFragment}'）。実際の連鎖: "
            + string.Join(" -> ", chain.Select(e => $"{e.GetType().Name}: {e.Message}")));
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
                "storage://normalized/pipe.md", [], 0, 0, []));
    }
}
