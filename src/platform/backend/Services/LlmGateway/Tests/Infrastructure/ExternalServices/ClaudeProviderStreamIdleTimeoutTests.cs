using System.Diagnostics;
using System.Net;
using System.Text;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-45, FR-11, IADR-0566 (#1997): 逐次（StreamAsync）の無通信の期限を、本番の生成点（AnthropicClientFactory）越しに固定する。
//   ①チャンクの途中で止まる相手 → 期限（1 秒）で TimeoutException。止まる前に届いた本文は流れている
//   ②呼び出し側の取り消し → OperationCanceledException（TimeoutException にしない）
//   ③期限より短い間隔でイベントが届き続ける相手 → 全体が期限を超えても打ち切らない（時計はイベントごとに戻る）
//   ④受け取り側（yield の先）が遅い → 打ち切らない（待つ間だけ時計を回す）
//   ⑤設定の解決（未設定は Llm:AnthropicTimeoutSeconds に従う・有効値・不正値は warn）
// 変異: 無通信の期限を外すと①が待ち続けて xUnit の上限で赤、時計を戻さないと③が赤、受け取り後に時計を止めないと④が赤。
[Trait("TestKind", "Unit")]
public class ClaudeProviderStreamIdleTimeoutTests
{
    private static readonly string Start = Event("message_start",
        """{"type":"message_start","message":{"id":"m","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"stop_reason":null,"usage":{"input_tokens":3,"output_tokens":0}}}""");

    private static readonly string BlockStart = Event("content_block_start",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");

    private static string TextDelta(string text) => Event("content_block_delta",
        "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}");

    private static readonly string End =
        Event("content_block_stop", """{"type":"content_block_stop","index":0}""")
        + Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":4}}""")
        + Event("message_stop", """{"type":"message_stop"}""");

    // IADR-0601 決定 1 (#2116): 経過報告のチャンク（本文なし・Done=false）。
    private static bool IsProgress(CompletionChunk chunk) => !chunk.Done && chunk.TextDelta.Length == 0;

    private static string Event(string name, string data) => $"event: {name}\ndata: {data}\n\n";

    private static Dictionary<string, string?> IdleOneSecond() =>
        new() { [AnthropicClientFactory.StreamIdleConfigKey] = "1" };

    private static CompletionRequest Request() => new("q", 512, "claude-sonnet-5-5", "rag-answer");

    // T-45 ①: ヘッダと本文の一部を返した後に止まる相手。期限（1 秒）で TimeoutException になり、止まる前の本文は流れている。
    // 期限を外すと待ち続け、下の Timeout で赤になる。
    [Fact(Timeout = 15000)]
    public async Task 途中で止まる相手は無通信の期限でTimeoutExceptionになる()
    {
        var handler = new ScriptedSseHandler([(TimeSpan.Zero, Start + BlockStart + TextDelta("途中まで"))], stallAtEnd: true);
        var provider = TestClaudeProviders.Create(handler, IdleOneSecond());

        var received = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        Exception? caught = null;
        try
        {
            // IADR-0601 (#2116): 本文なしの経過報告（Done=false でトークン数だけを持つ）は本文の並びから外して読む。
            await foreach (var chunk in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
                if (!IsProgress(chunk))
                    received.Add(chunk.TextDelta);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        stopwatch.Stop();

        received.Should().Equal("途中まで");
        caught.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain(AnthropicClientFactory.StreamIdleConfigKey);
        // CompletionUseCase は OperationCanceledException 以外を upstream_error へ縮退する。計器の上流状態は transport。
        caught.Should().NotBeAssignableTo<OperationCanceledException>();
        LlmCompletionMetrics.ClassifyUpstreamStatus(caught).Should().Be(LlmCompletionMetrics.UpstreamTransport);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        handler.Calls.Should().Be(1, "期限切れも再試行しない");
    }

    // T-45 ②: 呼び出し側の取り消しは期限切れと区別する（従前どおり OperationCanceledException）。
    [Fact(Timeout = 15000)]
    public async Task 呼び出し側の取り消しはTimeoutExceptionにしない()
    {
        var handler = new ScriptedSseHandler([(TimeSpan.Zero, Start + BlockStart)], stallAtEnd: true);
        var provider = TestClaudeProviders.Create(handler, new Dictionary<string, string?>
        {
            [AnthropicClientFactory.StreamIdleConfigKey] = "60",
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = async () =>
        {
            await foreach (var _ in provider.StreamAsync(Request(), caller.Token))
            {
            }
        };

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<TimeoutException>();
    }

    // T-45 ③: 期限（1 秒）より短い間隔（0.4 秒）でイベントが届き続ける相手は、全体（約 2 秒）が期限を超えても打ち切らない。
    // 時計を最初の 1 回だけ掛けて戻さない形にすると赤になる。
    [Fact(Timeout = 15000)]
    public async Task 期限より短い間隔で届き続ける相手は全体が期限を超えても打ち切らない()
    {
        var gap = TimeSpan.FromMilliseconds(400);
        var handler = new ScriptedSseHandler(
        [
            (TimeSpan.Zero, Start + BlockStart),
            (gap, TextDelta("一")),
            (gap, TextDelta("二")),
            (gap, TextDelta("三")),
            (gap, TextDelta("四")),
            (gap, End),
        ], stallAtEnd: false);
        var provider = TestClaudeProviders.Create(handler, IdleOneSecond());

        var chunks = new List<CompletionChunk>();
        var stopwatch = Stopwatch.StartNew();
        await foreach (var chunk in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5), "全体は期限を超えている（試験の前提）");
        string.Concat(chunks.Select(c => c.TextDelta)).Should().Be("一二三四");
        chunks.Last().Done.Should().BeTrue();
        chunks.Last().StopReason.Should().Be("end_turn");
    }

    // T-45 ④: 受け取り側（yield の先。gRPC への書き込み）が遅くても、上流を待っていない間の時間は数えない。
    // 受け取った後に時計を止めない形にすると赤になる。
    [Fact(Timeout = 15000)]
    public async Task 受け取り側が遅くても上流を待つ間だけを数える()
    {
        var handler = new ScriptedSseHandler(
            [
                (TimeSpan.Zero, Start + BlockStart + TextDelta("一")),
                // 受け取り側が遅い間に、上流は次の断片を用意し終えている（上流は止まっていない）。
                (TimeSpan.FromMilliseconds(100), TextDelta("二") + End),
            ], stallAtEnd: false);
        var provider = TestClaudeProviders.Create(handler, IdleOneSecond());

        var received = new List<string>();
        await foreach (var chunk in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
        {
            if (!IsProgress(chunk))
                received.Add(chunk.TextDelta);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
        }

        received.Should().Equal("一", "二", string.Empty);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    // T-45 ⑤: 未設定は一括の期限（Llm:AnthropicTimeoutSeconds の解決値）に従う。どちらも未設定なら 100 秒。
    [Fact]
    public void 未設定なら一括の期限と同じ値になる()
    {
        AnthropicClientFactory.StreamIdleConfigKey.Should().Be("Llm:AnthropicStreamIdleTimeoutSeconds");
        AnthropicClientFactory.ResolveStreamIdleTimeout(Config()).Should().Be(TimeSpan.FromSeconds(100));
        AnthropicClientFactory.ResolveStreamIdleTimeout(Config((AnthropicClientFactory.ConfigKey, "180")))
            .Should().Be(TimeSpan.FromSeconds(180));
        AnthropicClientFactory.ResolveStreamIdleTimeout(Config((AnthropicClientFactory.StreamIdleConfigKey, "  ")))
            .Should().Be(TimeSpan.FromSeconds(100));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 1 ", 1)]
    public void 有効値はその秒数を使う(string value, int expectedSeconds)
    {
        using var loggers = new RecordingLoggerFactory();

        AnthropicClientFactory.ResolveStreamIdleTimeout(
                Config((AnthropicClientFactory.StreamIdleConfigKey, value), (AnthropicClientFactory.ConfigKey, "180")),
                loggers.CreateLogger("t"))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        loggers.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("2147484")]
    public void 不正値は一括の期限と同じ値へ倒しwarnを1行出す(string value)
    {
        using var loggers = new RecordingLoggerFactory();

        AnthropicClientFactory.ResolveStreamIdleTimeout(
                Config((AnthropicClientFactory.StreamIdleConfigKey, value), (AnthropicClientFactory.ConfigKey, "180")),
                loggers.CreateLogger("t"))
            .Should().Be(TimeSpan.FromSeconds(180));
        loggers.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain(AnthropicClientFactory.StreamIdleConfigKey).And.Contain(value);
    }

    // 段ごとに待ってから SSE の断片を返す相手。stallAtEnd なら最後の断片の後、取り消されるまで止まる。
    private sealed class ScriptedSseHandler(IReadOnlyList<(TimeSpan Delay, string Text)> parts, bool stallAtEnd)
        : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var content = new StreamContent(new ScriptedStream(parts, stallAtEnd));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class ScriptedStream(IReadOnlyList<(TimeSpan Delay, string Text)> parts, bool stallAtEnd) : Stream
    {
        private int _index;
        private byte[] _pending = [];
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _pending.Length)
            {
                if (_index >= parts.Count)
                {
                    if (!stallAtEnd)
                        return 0;
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                var (delay, text) = parts[_index++];
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
                _pending = Encoding.UTF8.GetBytes(text);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
