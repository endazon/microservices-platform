using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Pricing;
using LlmGateway.Domain.Routing;
using LlmGateway.Features.Completions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;

namespace LlmGateway.Tests.Features.Completions;

// FR-11, NFR-17, NFR-19, NFR-28, IADR-0104 追記 (#1819): `Sent=false` の原因を事後に追えることを固定する。
//
// PoC で AST の取引判断が 132 件連続で `Sent=false` を受けたが、原因を確定できなかった。理由は 2 つ ——
// 越境拒否の枝がログを出さなかったこと、応答が原因を文言（Text）でしか区別していなかったこと。
//   - A1: 越境拒否は Warning を 1 行出す。同じ (用途, 理由) は 5 分の間抑え、次の発生で件数つきの要約を出す。
//   - A2: `Sent=false` の 6 経路（一括 3・逐次 3）すべてが `FailureKind` を名乗る。上流不調は HTTP 状態も載せる。
//
// 判定器（CompletionUseCase）を直接組む —— 端点を通すと、既定構成ではティアB が有効なため越境拒否が
// 起こらない（GrpcCompleteTests の注記と同じ実測）。ルータとプロバイダは試験側で決める。
// 計器は共有 Meter へ発行するので、規則どおり SharedMeterCollection に入る（IADR-0394）。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public sealed class SentFalseObservabilityTests : IDisposable
{
    private const string DenyReason = "機密区分 Restricted は許容ティア [A] に送信可能なエンドポイントが無いため送信を拒否";

    private readonly ServiceProvider _meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingLoggerFactory _logs = new();

    public void Dispose() => _meterServices.Dispose();

    private sealed class Static<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedRouter(RoutingDecision decision) : ILlmRouter
    {
        public RoutingDecision Route(RoutingRequest request) => decision;
    }

    // 上流が HTTP 状態つき（status あり）または輸送の失敗（status なし）で失敗するプロバイダ。
    private sealed class FailingProvider(HttpStatusCode? status) : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => throw Failure();

        public async IAsyncEnumerable<CompletionChunk> StreamAsync(
            CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw Failure();
#pragma warning disable CS0162 // 反復子にするための到達しない yield
            yield break;
#pragma warning restore CS0162
        }

        private HttpRequestException Failure() =>
            status is { } s ? new HttpRequestException("upstream", null, s) : new HttpRequestException("transport");
    }

    private sealed class OkProvider : ILlmProvider
    {
        public Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new CompletionResult("ok", 1, 1, CompletionStopReasons.EndTurn));
    }

    private static RoutingDecision Denied(string reason = DenyReason)
        => new(false, null, null, null, null, false, reason);

    private static RoutingDecision Allowed(string provider = "claude")
        => new(true, "claude-managed", provider, ProtectionTier.B, "test-model", false, "test route");

    private CompletionUseCase UseCase(RoutingDecision decision, ILlmProvider? provider = null)
    {
        var services = new ServiceCollection();
        if (provider is not null)
            services.AddKeyedSingleton("claude", provider);
        var meterFactory = _meterServices.GetRequiredService<IMeterFactory>();
        var routing = new LlmRoutingOptions();
        routing.PurposeModels["trade-decision"] = "test-model";
        var prices = new ModelPriceTable(
            new Static<ModelPricingOptions>(new ModelPricingOptions()), NullLogger<ModelPriceTable>.Instance);
        return new CompletionUseCase(
            new FixedRouter(decision),
            services.BuildServiceProvider(),
            _logs,
            new LlmCompletionMetrics(meterFactory, new Static<LlmRoutingOptions>(routing)),
            new LlmUsageMetrics(meterFactory, new Static<LlmRoutingOptions>(routing), prices, _time),
            new LogOccurrenceThrottle(_time));
    }

    private static CompletionApiRequest Request(string purpose = "trade-decision")
        => new("本文", MaxTokens: 100, Confidentiality: "restricted", Purpose: purpose);

    private IReadOnlyList<RecordingLoggerFactory.Entry> EgressDeniedLogs()
        => [.. _logs.Entries.Where(e => e.Message.StartsWith("LLM egress denied", StringComparison.Ordinal))];

    private static async Task<CompletionStreamEvent> LastEventAsync(IAsyncEnumerable<CompletionStreamEvent> events)
    {
        CompletionStreamEvent? last = null;
        await foreach (var ev in events)
            last = ev;
        return last!;
    }

    // ---- A1: 越境拒否のログ ----------------------------------------------------------------------

    [Fact]
    public async Task 越境拒否はWarningを1行出し理由と用途と機密区分を載せる()
    {
        var useCase = UseCase(Denied());

        await useCase.ExecuteAsync(Request(), isSynthetic: false, TestContext.Current.CancellationToken);

        var log = EgressDeniedLogs().Should().ContainSingle().Subject;
        log.Level.Should().Be(LogLevel.Warning);
        log.Exception.Should().BeNull();
        log.Values["Reason"].Should().Be(DenyReason);
        log.Values["Purpose"].Should().Be("trade-decision");
        log.Values["Sensitivity"].Should().Be(SensitivityClass.Restricted);
        log.Values["FailureKind"].Should().Be(CompletionFailureKinds.EgressDenied);
        log.Values["Suppressed"].Should().Be(0L);
    }

    [Fact]
    public async Task 同じ用途と理由の越境拒否は5分の間は抑え_次の発生で抑えた件数つきの要約を出す()
    {
        var useCase = UseCase(Denied());
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 5; i++)
            await useCase.ExecuteAsync(Request(), isSynthetic: false, ct);
        EgressDeniedLogs().Should().HaveCount(1, "初回だけ記録し、続く 4 回は抑える");

        _time.Advance(LogOccurrenceThrottle.SummaryInterval - TimeSpan.FromSeconds(1));
        await useCase.ExecuteAsync(Request(), isSynthetic: false, ct);
        EgressDeniedLogs().Should().HaveCount(1, "間隔に満たないうちは抑え続ける");

        _time.Advance(TimeSpan.FromSeconds(1));
        await useCase.ExecuteAsync(Request(), isSynthetic: false, ct);

        var logs = EgressDeniedLogs();
        logs.Should().HaveCount(2);
        logs[1].Values["Suppressed"].Should().Be(5L, "前回の記録以降に抑えた 5 件（4 件＋間隔直前の 1 件）を添える");
    }

    [Fact]
    public async Task 用途が違えば抑制の鍵が別になり_それぞれ初回を記録する()
    {
        var useCase = UseCase(Denied());
        var ct = TestContext.Current.CancellationToken;

        await useCase.ExecuteAsync(Request("trade-decision"), isSynthetic: false, ct);
        await useCase.ExecuteAsync(Request("default"), isSynthetic: false, ct);

        EgressDeniedLogs().Select(e => e.Values["Purpose"]).Should().Equal("trade-decision", "default");
    }

    [Fact]
    public async Task 逐次の越境拒否も同じ抑制器を通り_一括と鍵を共有する()
    {
        var useCase = UseCase(Denied());
        var ct = TestContext.Current.CancellationToken;

        var first = await LastEventAsync(useCase.StreamAsync(Request(), isSynthetic: false, ct));
        await useCase.ExecuteAsync(Request(), isSynthetic: false, ct);

        first.Sent.Should().BeFalse();
        EgressDeniedLogs().Should().ContainSingle(
            "逐次と一括は同じ (用途, 理由) なので、どちらが先でも 1 行にまとまる")
            .Which.Category.Should().Be(CompletionUseCase.StreamLoggerCategory);
    }

    // NFR-28: purpose は呼び出し側の自由文字列。改行を混ぜてもログ行を偽造できない。
    [Fact]
    public async Task 用途の改行はログへ出す前に無害化する()
    {
        var useCase = UseCase(Denied());

        await useCase.ExecuteAsync(
            Request("trade\nFAKE LOG LINE"), isSynthetic: false, TestContext.Current.CancellationToken);

        var purpose = (string)EgressDeniedLogs().Should().ContainSingle().Subject.Values["Purpose"]!;
        purpose.Should().NotContain("\n").And.Be("trade_FAKE LOG LINE");
    }

    // ---- A2: 応答の FailureKind（6 経路）---------------------------------------------------------

    [Fact]
    public async Task 一括_越境拒否はegress_deniedを名乗る()
    {
        var resp = await UseCase(Denied()).ExecuteAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken);

        resp.Sent.Should().BeFalse();
        resp.FailureKind.Should().Be(CompletionFailureKinds.EgressDenied);
        resp.UpstreamStatusCode.Should().BeNull();
        resp.RoutingReason.Should().Be(DenyReason, "RoutingReason の意味は変えない");
        resp.Text.Should().Be(DenyReason, "Text の意味は変えない");
    }

    [Fact]
    public async Task 逐次_越境拒否はegress_deniedを名乗る()
    {
        var done = await LastEventAsync(UseCase(Denied()).StreamAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken));

        done.Done.Should().BeTrue();
        done.Sent.Should().BeFalse();
        done.FailureKind.Should().Be(CompletionFailureKinds.EgressDenied);
        done.UpstreamStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task 一括_プロバイダ未登録はprovider_missingを名乗る()
    {
        var resp = await UseCase(Allowed("not-registered")).ExecuteAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken);

        resp.Sent.Should().BeFalse();
        resp.FailureKind.Should().Be(CompletionFailureKinds.ProviderMissing);
        resp.UpstreamStatusCode.Should().BeNull();
        EgressDeniedLogs().Should().BeEmpty("越境拒否のログは越境拒否の枝だけが出す");
    }

    [Fact]
    public async Task 逐次_プロバイダ未登録はprovider_missingを名乗る()
    {
        var done = await LastEventAsync(UseCase(Allowed("not-registered")).StreamAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken));

        done.Sent.Should().BeFalse();
        done.FailureKind.Should().Be(CompletionFailureKinds.ProviderMissing);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(null, null)]
    public async Task 一括_上流不調はupstream_errorと上流のHTTP状態を名乗る(HttpStatusCode? status, int? expected)
    {
        var resp = await UseCase(Allowed(), new FailingProvider(status)).ExecuteAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken);

        resp.Sent.Should().BeFalse();
        resp.FailureKind.Should().Be(CompletionFailureKinds.UpstreamError);
        resp.UpstreamStatusCode.Should().Be(expected, "輸送の失敗（状態なし）は null");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(null, null)]
    public async Task 逐次_上流不調はupstream_errorと上流のHTTP状態を名乗る(HttpStatusCode? status, int? expected)
    {
        var done = await LastEventAsync(UseCase(Allowed(), new FailingProvider(status)).StreamAsync(
            Request(), isSynthetic: false, TestContext.Current.CancellationToken));

        done.Sent.Should().BeFalse();
        done.FailureKind.Should().Be(CompletionFailureKinds.UpstreamError);
        done.UpstreamStatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task 送信が成立した応答とイベントは原因を持たない()
    {
        var useCase = UseCase(Allowed(), new OkProvider());
        var ct = TestContext.Current.CancellationToken;

        var resp = await useCase.ExecuteAsync(Request(), isSynthetic: false, ct);
        var done = await LastEventAsync(useCase.StreamAsync(Request(), isSynthetic: false, ct));

        resp.Sent.Should().BeTrue();
        resp.FailureKind.Should().BeNull();
        resp.UpstreamStatusCode.Should().BeNull();
        done.Sent.Should().BeTrue();
        done.FailureKind.Should().BeNull();
        done.UpstreamStatusCode.Should().BeNull();
    }

    // 応答の語彙は計器の llm.result と同じ文字列である（計器の定数は契約の定数を引く）。
    [Fact]
    public void 応答の原因の語彙は計器のllm_resultと同じ文字列である()
    {
        LlmCompletionMetrics.ResultEgressDenied.Should().Be(CompletionFailureKinds.EgressDenied).And.Be("egress_denied");
        LlmCompletionMetrics.ResultProviderMissing.Should().Be(CompletionFailureKinds.ProviderMissing).And.Be("provider_missing");
        LlmCompletionMetrics.ResultUpstreamError.Should().Be(CompletionFailureKinds.UpstreamError).And.Be("upstream_error");
    }

    // ---- A3: gRPC の写像（proto3 に null は無い。null ↔ 空文字 / 0）---------------------------------

    [Fact]
    public void 写像は原因の種類と上流の状態を往復させ_nullは空文字と0へ写す()
    {
        var failed = new CompletionApiResponse("x", "m", 0, 0, Sent: false, Endpoint: "e", RoutingReason: "r",
            FailureKind: CompletionFailureKinds.UpstreamError, UpstreamStatusCode: 503);
        var sent = new CompletionApiResponse("x", "m", 1, 1);

        var failedPb = LlmGrpcMapping.ToProto(failed);
        var sentPb = LlmGrpcMapping.ToProto(sent);

        failedPb.FailureKind.Should().Be("upstream_error");
        failedPb.UpstreamStatusCode.Should().Be(503);
        sentPb.FailureKind.Should().BeEmpty();
        sentPb.UpstreamStatusCode.Should().Be(0);
        LlmGrpcMapping.ToDto(failedPb).Should().Be(failed);
        LlmGrpcMapping.ToDto(sentPb).Should().Be(sent);
    }

    [Fact]
    public void 写像は逐次イベントの原因の種類と上流の状態も往復させる()
    {
        var done = new CompletionStreamEvent(string.Empty, Done: true, Sent: false, Text: "t",
            RoutingReason: "r", FailureKind: CompletionFailureKinds.EgressDenied);
        var delta = new CompletionStreamEvent("d");

        var donePb = LlmGrpcMapping.ToProto(done);

        donePb.FailureKind.Should().Be("egress_denied");
        donePb.UpstreamStatusCode.Should().Be(0);
        LlmGrpcMapping.ToDto(donePb).Should().Be(done);
        LlmGrpcMapping.ToDto(
            LlmGrpcMapping.ToProto(delta)).Should().Be(delta);
    }
}
