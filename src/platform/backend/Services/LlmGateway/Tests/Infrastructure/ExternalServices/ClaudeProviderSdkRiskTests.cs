using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Domain.Routing;
using LlmGateway.Infrastructure.ExternalServices;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-43, FR-11, ADR-0038 決定 4, IADR-0225, IADR-0374, IADR-0528, IADR-0562 (#1749): 公式 SDK へ移したことで
// **黙って**変わり得る 3 点を、本番の生成点（AnthropicClientFactory）越しに固定する。
//
//   1. 例外の型: SDK は AnthropicApiException を投げる（HttpRequestException ではない）。包み直さないと
//      LlmFallbackPolicy.StatusCodeOf が null になり、400 系のフォールバックも 429 の区別も消える（例外も赤も出ない）。
//   2. 再試行: SDK 既定は 2 回で 429・5xx・接続失敗・期限切れも再試行する。旧 SDK は 1 回だった（実測。作業仕様書）。
//   3. 期限: SDK 既定は max_tokens からの算出（最大 10 分・ストリームは最大 1 時間）。`Llm:AnthropicTimeoutSeconds` が効くこと。
//
// 変異: ①ClaudeProvider の包み直しを外す → 1 の試験が赤、②生成点の MaxRetries を外す → 2 の試験が赤、
//       ③生成点の Timeout を外す → 3 の試験が赤（作業仕様書・PR に実測を記録）。
[Trait("TestKind", "Unit")]
public class ClaudeProviderSdkRiskTests
{
    private const string ErrorBody = """{"type":"error","error":{"type":"invalid_request_error","message":"m"}}""";

    private const string Sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"m","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private static Task<Exception> CompleteFailure(ClaudeProvider provider)
        => Catch(() => provider.CompleteAsync(new CompletionRequest("q", 512, "claude-opus-5-5", "analysis"), TestContext.Current.CancellationToken));

    private static Task<Exception> StreamFailure(ClaudeProvider provider)
        => Catch(async () =>
        {
            await foreach (var _ in provider.StreamAsync(new CompletionRequest("q", 512, "claude-opus-5-5", "analysis"), TestContext.Current.CancellationToken))
            {
            }
        });

    private static async Task<Exception> Catch(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("例外が出なかった");
    }

    public static TheoryData<HttpStatusCode, bool> FallbackStatuses() => new()
    {
        { HttpStatusCode.BadRequest, true },
        { HttpStatusCode.NotFound, true },
        { HttpStatusCode.RequestEntityTooLarge, true },
        { HttpStatusCode.UnprocessableEntity, true },
        { HttpStatusCode.TooManyRequests, false },      // ★ 429 は再試行であってフォールバックではない（ADR-0038 決定 4）
        { HttpStatusCode.InternalServerError, false },
        { (HttpStatusCode)529, false },                  // overloaded
    };

    // T-43 ①: 非ストリームの上流失敗は HttpRequestException(StatusCode) として届き、判定器がそのまま読める。
    [Theory]
    [MemberData(nameof(FallbackStatuses))]
    public async Task CompleteAsync_UpstreamStatus_ReachesFallbackPolicyAsHttpRequestException(HttpStatusCode status, bool fallBack)
    {
        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(ErrorBody, status: status));

        var ex = await CompleteFailure(provider);

        ex.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(status);
        LlmFallbackPolicy.StatusCodeOf(ex).Should().Be((int)status);
        LlmFallbackPolicy.ShouldFallBack(ex).Should().Be(fallBack);
    }

    // T-43 ①: ストリームでも同じ（要求の送信は最初の MoveNextAsync で起きる）。
    [Theory]
    [MemberData(nameof(FallbackStatuses))]
    public async Task StreamAsync_UpstreamStatus_ReachesFallbackPolicyAsHttpRequestException(HttpStatusCode status, bool fallBack)
    {
        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(ErrorBody, status: status));

        var ex = await StreamFailure(provider);

        ex.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().Be(status);
        LlmFallbackPolicy.ShouldFallBack(ex).Should().Be(fallBack);
    }

    // T-43 ①: 上流の状態の計器の軸（IADR-0374）も旧 SDK と同じ値になる（429 が他の失敗と区別できる）。
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, LlmCompletionMetrics.UpstreamRateLimited)]
    [InlineData(HttpStatusCode.BadRequest, LlmCompletionMetrics.UpstreamClientError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LlmCompletionMetrics.UpstreamServerError)]
    public async Task CompleteAsync_UpstreamStatus_ClassifiesLikeOldSdk(HttpStatusCode status, string expected)
    {
        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(ErrorBody, status: status));

        LlmCompletionMetrics.ClassifyUpstreamStatus(await CompleteFailure(provider)).Should().Be(expected);
    }

    // T-43 ①: 送信の失敗（接続断）は状態なしの HttpRequestException ＝ transport。フォールバックしない。
    [Fact]
    public async Task CompleteAsync_ConnectionFailure_IsTransportWithoutStatus()
    {
        var handler = new ThrowingHandler(() => new HttpRequestException("connection refused"));
        var provider = TestClaudeProviders.Create(handler);

        var ex = await CompleteFailure(provider);

        ex.Should().BeOfType<HttpRequestException>().Which.StatusCode.Should().BeNull();
        LlmFallbackPolicy.ShouldFallBack(ex).Should().BeFalse();
        LlmCompletionMetrics.ClassifyUpstreamStatus(ex).Should().Be(LlmCompletionMetrics.UpstreamTransport);
    }

    // T-43 ②: 再試行しない。429・500・529 で送信は 1 回（旧 SDK の実測と同じ）。非ストリーム・ストリームとも。
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData((HttpStatusCode)529)]
    public async Task UpstreamRetryableStatus_IsSentOnce(HttpStatusCode status)
    {
        var complete = new StubAnthropicHandler(ErrorBody, status: status);
        var stream = new StubAnthropicHandler(ErrorBody, status: status);

        await CompleteFailure(TestClaudeProviders.Create(complete));
        await StreamFailure(TestClaudeProviders.Create(stream));

        complete.Calls.Should().Be(1, "SDK の既定（2 回の再試行）を生成点が 0 に変えている");
        stream.Calls.Should().Be(1);
    }

    // T-43 ②: 接続失敗も再試行しない（SDK 既定では再試行される種類）。
    [Fact]
    public async Task ConnectionFailure_IsSentOnce()
    {
        var handler = new ThrowingHandler(() => new HttpRequestException("connection refused"));

        await CompleteFailure(TestClaudeProviders.Create(handler));

        handler.Calls.Should().Be(1);
    }

    // T-43 ③: 期限は `Llm:AnthropicTimeoutSeconds`。1 秒にして応答ヘッダを返さない相手へ送ると、約 1 秒で
    // TaskCanceledException（内側 TimeoutException）になり、送信は 1 回（期限切れも再試行しない）。
    // SDK 既定（max_tokens 512 なら 30 秒）のままだと、この試験は 30 秒待って赤になる。
    [Fact]
    public async Task ConfiguredTimeout_CancelsHangingCallOnceWithoutRetry()
    {
        var handler = new HangingHandler();
        var provider = TestClaudeProviders.Create(
            handler, new Dictionary<string, string?> { [AnthropicClientFactory.ConfigKey] = "1" });

        var stopwatch = Stopwatch.StartNew();
        var ex = await CompleteFailure(provider);
        stopwatch.Stop();

        ex.Should().BeAssignableTo<TaskCanceledException>().Which.InnerException.Should().BeOfType<TimeoutException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        handler.Calls.Should().Be(1);
    }

    // T-43 ③: 期限を明示するので、SDK の「非ストリームで max_tokens からの算出が 10 分を超えたら例外」（max_tokens > 20,000）に当たらない。
    [Fact]
    public async Task CompleteAsync_LargeMaxTokens_DoesNotTripSdkStreamingRequirement()
    {
        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(
            """{"id":"m","type":"message","role":"assistant","model":"claude-opus-5-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""));

        var result = await provider.CompleteAsync(new CompletionRequest("q", 64000, "claude-opus-5-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be("ok");
    }

    // T-43: 正常系の陽性対照（ストリームのスタブが例外を出さずに終わる＝上の失敗の試験がスタブの不備で赤になっていない）。
    [Fact]
    public async Task StreamAsync_Success_IsSentOnce()
    {
        var handler = new StubAnthropicHandler(Sse, "text/event-stream");
        var provider = TestClaudeProviders.Create(handler);

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(new CompletionRequest("q"), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        chunks.Should().ContainSingle(c => c.Done).Which.StopReason.Should().Be("end_turn");
        handler.Calls.Should().Be(1);
    }

    private sealed class ThrowingHandler(Func<Exception> failure) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw failure();
        }
    }

    // 応答ヘッダを返さず、取り消されるまで待つ相手。
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        }
    }
}
