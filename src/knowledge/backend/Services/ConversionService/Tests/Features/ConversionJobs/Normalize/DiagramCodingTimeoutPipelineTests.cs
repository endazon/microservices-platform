using System.Diagnostics;
using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.Configuration;
using ConversionService.Infrastructure.ExternalServices;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

// UC-06 テスト仕様 T-46〜T-48 (#1621) —— FR-12, UC-06 例外フロー「図コード化（LLM）の失敗は画像保持へ縮退し、
// 後日の人手補正・再登録でコード化する」, ADR-0012, IADR-0008 決定 B-2（2026-09-27 追記）:
// **LLM ゲートウェイが応答しなくても、受け口の実行期限の内に変換が成功で終わること**を、受け口から端まで測る。
//
// 🔴 **受け口の ct は Wolverine の 1 通ごとの実行期限を含む。** Wolverine は受け口へ渡す ct を「実行期限の CTS」と
// 「停止要求」の連結で作る。本試験はそれを**縮尺した期限つきの ct**（`DiagramCodingLimits.HandlerTimeout` で CancelAfter）
// として受け口へ渡す —— 試験の中で「呼び出し元の ct は立っていない」状態を作ると、本番では届かない縮退の枝を
// 測ってしまう（#1621 の監査が実測した抜け。50 ms の期限つき ct を渡すと、旧版の試験器ではジョブが失敗した）。
//
// 本物を通す部品: `RawDocumentFetchedConsumer` → `NormalizationService`（総枠つき）→ **本番と同じ登録**
// （`DiagramCoderRegistration.AddRestDiagramCoder`。名前付きクライアントの `Timeout` はここで決まる）の
// `LlmGatewayDiagramCoder` → `HttpClient`。差し替えるのは LLM ゲートウェイ（応答しないハンドラ）・本文変換・
// オブジェクトストレージ・発行口だけである。期限は本番の既定（20 秒 / 120 秒 / 300 秒）を 1 秒 / 2 秒 / 4 秒へ縮尺する
// （構成の下限が 1 秒なので、これより縮めない）。
[Trait("TestKind", "Unit")]
public class DiagramCodingTimeoutPipelineTests
{
    // 縮尺した期限: 1 回 1 秒・総枠 2 秒・受け口 4 秒（本番の既定と同じく 受け口 ＞ 総枠 ＋ 1 回）。
    private static readonly Dictionary<string, string?> ScaledLimits = new()
    {
        [DiagramCodingLimits.CallTimeoutKey] = "1",
        [DiagramCodingLimits.BudgetKey] = "2",
        [DiagramCodingLimits.HandlerTimeoutKey] = "4",
    };

    private static RawDocumentFetched Raw() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/design.docx", "storage://bucket/raw/design.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["knowledge-mgmt"],
            DateTimeOffset.UtcNow);

    // T-46: 図 1 つ・ゲートウェイは応答しない。1 回の期限で画像保持へ縮退し、受け口の期限より**前に**成功で確定する。
    [Fact]
    public async Task Hung_gateway_keeps_the_figure_as_an_image_before_the_handler_timeout_fires()
    {
        var gateway = new HangingGatewayHandler();
        await using var pipeline = new Pipeline(gateway, figureCount: 1);
        var ev = Raw();
        using var handler = pipeline.HandlerScopedToken();

        await pipeline.HandleAsync(ev, handler.Token);

        handler.IsCancellationRequested.Should().BeFalse("変換は受け口の実行期限より前に終わるべきである");
        var job = (await pipeline.ReadJobAsync(ev.FetchId))!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.Error.Should().BeNull();
        job.DiagramsCoded.Should().Be(0);
        job.DiagramsRetained.Should().Be(1);
        gateway.Requests.Should().Be(1);
        pipeline.Store.SavedAssets.Should().ContainSingle().Which.Should().EndWith("/assets/fig-1.png");
        pipeline.Store.LastMarkdown.Should().Contain("![fig-1](");
        pipeline.Publisher.Calls.Should().ContainSingle()
            .Which.AssetUris.Should().ContainSingle().Which.Should().EndWith("/assets/fig-1.png");
    }

    // T-48: 図 5 つ・ゲートウェイは応答しない。1 回 1 秒の期限が積もって総枠（2 秒）を使い切り、**残りの図はゲートウェイを
    // 呼ばずに**画像として残す（通常は 2 回で使い切る）。総枠が無ければ 5 回 × 1 秒 ＝ 5 秒で受け口の期限（4 秒）を越え、ジョブは失敗する。
    [Fact]
    public async Task Exhausted_budget_retains_the_remaining_figures_without_calling_the_gateway()
    {
        var gateway = new HangingGatewayHandler();
        await using var pipeline = new Pipeline(gateway, figureCount: 5);
        var ev = Raw();
        using var handler = pipeline.HandlerScopedToken();

        await pipeline.HandleAsync(ev, handler.Token);

        handler.IsCancellationRequested.Should().BeFalse("総枠があれば受け口の実行期限より前に終わる");
        // 回数は負荷で揺れる（1 回目が期限 1 秒 ＋ 待ち行列の遅れで総枠 2 秒を越えれば 1 回で使い切る。手元の全体実行で 1 回を実測）。
        // 固定するのは「少なくとも 1 回は呼び、全 5 図は呼ばない」こと。総枠が無ければ 5 図すべてを呼ぶ（変異 M7）。
        gateway.Requests.Should().BeInRange(1, 3, "総枠を使い切った残りの図はゲートウェイを呼ばない");
        var job = (await pipeline.ReadJobAsync(ev.FetchId))!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.DiagramsRetained.Should().Be(5);
        pipeline.Store.SavedAssets.Should().HaveCount(5);
    }

    // T-47（T-46 の対照）: 呼び出し元（受け口の ct）の取り消しは畳まずに受け口の外へ出す。
    // 図を画像として保管せず、発行もしない —— 停止要求・実行期限の最中に「変換成功」を作らない。
    [Fact]
    public async Task Caller_cancellation_propagates_out_of_the_consumer()
    {
        using var handler = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var pipeline = new Pipeline(new HangingGatewayHandler(onSend: handler.Cancel), figureCount: 1);
        var ev = Raw();

        var act = () => pipeline.HandleAsync(ev, handler.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(handler.Token);
        pipeline.Store.SavedAssets.Should().BeEmpty();
        pipeline.Publisher.Calls.Should().BeEmpty();
    }

    // 受け口から LLM ゲートウェイの手前までを本物で組む。図のコード化は本番と同じ登録から解決する。
    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly DbContextOptions<ConversionJobDbContext> _options =
            new DbContextOptionsBuilder<ConversionJobDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        private readonly ServiceProvider _services;
        private readonly int _figureCount;
        private ConversionJobDbContext? _handlerDb;

        public Pipeline(HttpMessageHandler gateway, int figureCount)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(ScaledLimits).Build();
            Limits = DiagramCodingLimits.From(configuration);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRestDiagramCoder(configuration, Limits).ConfigurePrimaryHttpMessageHandler(() => gateway);
            _services = services.BuildServiceProvider();
            _figureCount = figureCount;
        }

        public DiagramCodingLimits Limits { get; }
        public RecordingObjectStore Store { get; } = new();
        public RecordingDocumentNormalizedPublisher Publisher { get; } = new();

        // Wolverine が受け口へ渡す ct の縮尺版（実行期限の CTS と停止要求＝試験の ct の連結）。
        public CancellationTokenSource HandlerScopedToken()
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(Limits.HandlerTimeout);
            return cts;
        }

        public Task HandleAsync(RawDocumentFetched ev, CancellationToken ct)
        {
            _handlerDb?.Dispose();
            _handlerDb = new ConversionJobDbContext(_options);
            var normalizer = new NormalizationService(
                new FiguresBodyConverter(_figureCount),
                _services.GetRequiredService<IDiagramCoder>(),
                Store,
                NullLogger<NormalizationService>.Instance,
                Limits);
            var consumer = new RawDocumentFetchedConsumer(
                normalizer, Publisher, new EfConversionJobStore(_handlerDb),
                NullLogger<RawDocumentFetchedConsumer>.Instance);
            return consumer.Handle(ev, new Envelope { Attempts = 1 }, ct);
        }

        public async Task<ConversionJobDto?> ReadJobAsync(Guid fetchId)
        {
            await using var db = new ConversionJobDbContext(_options);
            return await new EfConversionJobStore(db).GetAsync(fetchId, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _handlerDb?.Dispose();
            await _services.DisposeAsync();
        }
    }

    // 応答を返さず、要求の ct が立つまで待つ（LLM ゲートウェイが応答しない状態）。届いた要求の数を数える。
    // `onSend` は要求が届いた時点で呼ぶ（呼び出し元の取り消しを「要求の途中」で起こすため）。
    private sealed class HangingGatewayHandler(Action? onSend = null) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            onSend?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException("the delay only ends by cancellation");
        }
    }

    // 図を n 個含む本文（目印つき。図は本文中の元の位置へ埋め込まれる）。
    private sealed class FiguresBodyConverter(int count) : IBodyConverter
    {
        public Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
            CancellationToken ct = default)
        {
            var ids = Enumerable.Range(1, count).Select(i => $"fig-{i}").ToList();
            var markdown = "# 設計\n\n" + string.Concat(ids.Select(id => $"![{id}](figure:{id})\n\n"));
            return Task.FromResult(new BodyConversionResult(
                markdown, [.. ids.Select(id => new ExtractedFigure(id, "image/png", [1, 2, 3]))]));
        }
    }

    private sealed class RecordingObjectStore : IObjectStore
    {
        public string LastMarkdown { get; private set; } = string.Empty;
        public List<string> SavedAssets { get; } = [];

        public Task<string> SaveMarkdownAsync(string key, string markdown, CancellationToken ct = default)
        {
            LastMarkdown = markdown;
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string> SaveAssetAsync(string key, byte[] bytes, string contentType,
            CancellationToken ct = default)
        {
            SavedAssets.Add(key);
            return Task.FromResult($"storage://normalized/{key}");
        }

        public Task<string?> TryGetMarkdownAsync(string uri, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
