using AwesomeAssertions;
using ConversionService.Domain;
using ConversionService.Domain.Ports;
using ConversionService.Features.ConversionJobs.Normalize;
using ConversionService.Infrastructure.ExternalServices;
using ConversionService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace ConversionService.Tests.Features.ConversionJobs.Normalize;

// UC-06 テスト仕様 T-46 / T-47 (#1621) —— FR-12, UC-06 例外フロー「図コード化（LLM）の失敗は画像保持へ縮退し、
// 後日の人手補正・再登録でコード化する」, ADR-0012, IADR-0008 決定 B-2:
// **LLM ゲートウェイの時間切れで、メッセージ消費の正規化全体が失敗しないこと**を、受け口から端まで測る。
//
// 本物を通す部品: `RawDocumentFetchedConsumer`（Wolverine の受け口）→ `NormalizationService` →
// **REST の図のコード化 `LlmGatewayDiagramCoder`**（`Services:LlmGatewayGrpc` 未構成時の既定の実装）→
// `HttpClient`（本物の `Timeout`）。差し替えるのは LLM ゲートウェイ（応答しないハンドラ）・本文変換・
// オブジェクトストレージ・発行口だけである。
//
// 🔴 従前は図のコード化が例外の型だけで絞っており、`HttpClient.Timeout` の `TaskCanceledException` が
// 受け口まで漏れて、ジョブは `failed`（再試行 → 使い切ればデッドレター）になっていた。
// **コード化の単体試験（FR-12 T-44）だけでは「正規化が続く」ことは言えない**ので、端から端の形でも置く。
[Trait("TestKind", "Unit")]
public class DiagramCodingTimeoutPipelineTests
{
    private static RawDocumentFetched Raw() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "filesystem", "/docs/design.docx", "storage://bucket/raw/design.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            new Dictionary<string, string> { ["confidentiality"] = "internal" }, ["knowledge-mgmt"],
            DateTimeOffset.UtcNow);

    // T-46: 時間切れ（呼び出し元の ct は立っていない）は図を画像として残し、正規化は成功で確定する。
    [Fact]
    public async Task Gateway_timeout_keeps_the_figure_as_an_image_and_the_job_succeeds()
    {
        await using var pipeline = new Pipeline(
            new HangingGatewayHandler(), gatewayTimeout: TimeSpan.FromMilliseconds(100));
        var ev = Raw();

        await pipeline.HandleAsync(ev, TestContext.Current.CancellationToken);

        var job = (await pipeline.ReadJobAsync(ev.FetchId))!;
        job.Status.Should().Be(ConversionJobStatus.Succeeded);
        job.Error.Should().BeNull();
        job.DiagramsCoded.Should().Be(0);
        job.DiagramsRetained.Should().Be(1);
        pipeline.Store.SavedAssets.Should().ContainSingle().Which.Should().EndWith("/assets/fig-1.png");
        pipeline.Store.LastMarkdown.Should().Contain("![fig-1](");
        var published = pipeline.Publisher.Calls.Should().ContainSingle().Which;
        published.AssetUris.Should().ContainSingle().Which.Should().EndWith("/assets/fig-1.png");
    }

    // T-47（T-46 の対照）: 呼び出し元（メッセージ消費）の取り消しは畳まずに受け口の外へ出す。
    // 図を画像として保管せず、発行もしない —— 停止要求の最中に「変換成功」を作らない。
    [Fact]
    public async Task Caller_cancellation_propagates_out_of_the_consumer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var pipeline = new Pipeline(
            new HangingGatewayHandler(onSend: cts.Cancel), gatewayTimeout: TimeSpan.FromSeconds(30));
        var ev = Raw();

        var act = () => pipeline.HandleAsync(ev, cts.Token);

        var thrown = await act.Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token);
        pipeline.Store.SavedAssets.Should().BeEmpty();
        pipeline.Publisher.Calls.Should().BeEmpty();
    }

    // 受け口から LLM ゲートウェイの手前までを本物で組む。
    private sealed class Pipeline(HttpMessageHandler gateway, TimeSpan gatewayTimeout) : IAsyncDisposable
    {
        private readonly DbContextOptions<ConversionJobDbContext> _options =
            new DbContextOptionsBuilder<ConversionJobDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        private readonly HttpClient _http = new(gateway)
        {
            BaseAddress = new Uri("http://llm-gateway:5007"),
            Timeout = gatewayTimeout,
        };

        private ConversionJobDbContext? _handlerDb;

        public RecordingObjectStore Store { get; } = new();
        public RecordingDocumentNormalizedPublisher Publisher { get; } = new();

        public Task HandleAsync(RawDocumentFetched ev, CancellationToken ct)
        {
            _handlerDb?.Dispose();
            _handlerDb = new ConversionJobDbContext(_options);
            var normalizer = new NormalizationService(
                new OneFigureBodyConverter(),
                new LlmGatewayDiagramCoder(_http, NullLogger<LlmGatewayDiagramCoder>.Instance),
                Store,
                NullLogger<NormalizationService>.Instance);
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

        public ValueTask DisposeAsync()
        {
            _handlerDb?.Dispose();
            _http.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // 応答を返さず、要求の ct が立つまで待つ（LLM ゲートウェイが応答しない状態）。
    // `onSend` は要求が届いた時点で呼ぶ（呼び出し元の取り消しを「要求の途中」で起こすため）。
    private sealed class HangingGatewayHandler(Action? onSend = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }

    // 図を 1 つ含む本文（目印つき。図は本文中の元の位置へ埋め込まれる）。
    private sealed class OneFigureBodyConverter : IBodyConverter
    {
        public Task<BodyConversionResult> ConvertAsync(string storageUri, string contentType,
            CancellationToken ct = default) =>
            Task.FromResult(new BodyConversionResult(
                "# 設計\n\n![fig-1](figure:fig-1)\n", [new ExtractedFigure("fig-1", "image/png", [1, 2, 3])]));
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
