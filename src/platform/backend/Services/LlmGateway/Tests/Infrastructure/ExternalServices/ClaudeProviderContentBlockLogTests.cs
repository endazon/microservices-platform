using System.Diagnostics.Metrics;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Domain.Ports;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-41, FR-11, NFR-19, ADR-0006, IADR-0114 決定 5, IADR-0535, IADR-0562 決定 5 (#1749): 本文に使わず捨てた content ブロックの
// ログ段階と件数の計器を固定する。
//
// 5.5 系は thinking を無効にできず（IADR-0531）、thinking を捨てるのは毎応答の通常の形である。
//   - thinking / redacted_thinking だけを捨て、本文（text ブロック）が残る → Debug
//   - 想定外の型を捨てた／本文が 1 個も無い                                → Warning
//   - 件数は型を問わず llm.anthropic.content_blocks_dropped で数える
// ［2026-10-17 / #1749］旧 SDK のときは委譲ハンドラ（AnthropicResponseSanitizingHandler）が SDK の解釈前に除去して数えていた。
// 公式 SDK は未知の型も保持するので、数えてログを出す場所は ClaudeProvider へ移った（条件・計器・値域は不変）。
// 計器は Meter の**インスタンス**で購読する（[[IADR-0394]] 決定 1）。共有 Meter 名へ発行するので加入もする。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public class ClaudeProviderContentBlockLogTests
{
    private const string Category = "LlmGateway.Infrastructure.ExternalServices.ClaudeProvider";

    // 本文の文字列はログに載ってはならない（型名のみ。IADR-0114 決定 5）ので、ログの文言と紛れない値にする。
    private const string BodyText = "回答の実体XYZ";
    private const string TextBlock = """{"type":"text","text":"回答の実体XYZ"}""";
    private const string ThinkingBlock = """{"type":"thinking","thinking":"","signature":"s"}""";
    private const string RedactedThinkingBlock = """{"type":"redacted_thinking","data":"opaque"}""";
    private const string ServerToolUseBlock = """{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}""";
    private const string FutureBlock = """{"type":"some_future_block","payload":1}""";

    private static string Envelope(string blocks) =>
        "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-5-5\","
        + "\"content\":[" + blocks + "],\"stop_reason\":\"end_turn\","
        + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

    private sealed record Measured(long Value, string BlockType);

    // 指定した Meter インスタンスの除去カウンタだけを集める。
    private sealed class Probe : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measured> _items = [];

        public Probe(Meter meter)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, meter)
                        && instrument.Name == AnthropicContentBlockMetrics.DroppedCounterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var type = string.Empty;
                foreach (var tag in tags)
                {
                    if (tag.Key == AnthropicContentBlockMetrics.BlockTypeTag)
                        type = tag.Value?.ToString() ?? string.Empty;
                }

                lock (_items) _items.Add(new Measured(value, type));
            });
            _listener.Start();
        }

        public IReadOnlyDictionary<string, long> CountsByType()
        {
            lock (_items)
                return _items.GroupBy(m => m.BlockType).ToDictionary(g => g.Key, g => g.Sum(m => m.Value));
        }

        public void Dispose() => _listener.Dispose();
    }

    private static async Task<(IReadOnlyList<RecordingLoggerFactory.Entry> Logs, IReadOnlyDictionary<string, long> Counts, string Text)>
        CompleteAsync(string body)
    {
        using var loggers = new RecordingLoggerFactory();
        var metrics = TestContentBlockMetrics.Create(out var meterFactory);
        using var probe = new Probe(meterFactory.Create(AnthropicContentBlockMetrics.MeterName));

        var provider = TestClaudeProviders.Create(new StubAnthropicHandler(body), metrics: metrics, loggers: loggers);
        var result = await provider.CompleteAsync(
            new CompletionRequest("q", 1024, "claude-haiku-5-5"), TestContext.Current.CancellationToken);

        return (loggers.Entries.Where(e => e.Category == Category).ToList(), probe.CountsByType(), result.Text);
    }

    // T-41: thinking だけを捨てるのは想定内（5.5 系の毎応答）。Debug 1 行・Warning 0 行、件数は計器へ。
    [Fact]
    public async Task Thinkingを捨てて本文が残るときはDebugで出しWarningを出さない()
    {
        var (logs, counts, text) = await CompleteAsync(Envelope($"{ThinkingBlock},{TextBlock}"));

        text.Should().Be(BodyText);
        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        logs.Single().Message.Should().Contain("thinking");
        counts.Should().Equal(new Dictionary<string, long> { [AnthropicContentBlockMetrics.TypeThinking] = 1 });
    }

    // T-41: redacted_thinking も想定内。型ごとに数える。
    [Fact]
    public async Task RedactedThinkingも想定内として扱い型ごとに数える()
    {
        var (logs, counts, _) = await CompleteAsync(Envelope($"{RedactedThinkingBlock},{ThinkingBlock},{TextBlock}"));

        logs.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logs.Should().ContainSingle(e => e.Level == LogLevel.Debug);
        counts.Should().Equal(new Dictionary<string, long>
        {
            [AnthropicContentBlockMetrics.TypeThinking] = 1,
            [AnthropicContentBlockMetrics.TypeRedactedThinking] = 1,
        });
    }

    // T-41: 想定外の型（server_tool_use・SDK の判別子に無い将来型）は Warning。型名を構造化値に載せ、計器は other へ集約する。
    [Fact]
    public async Task 想定外の型を捨てたときはWarningで型名を出す()
    {
        var (logs, counts, text) = await CompleteAsync(Envelope($"{ThinkingBlock},{ServerToolUseBlock},{FutureBlock},{TextBlock}"));

        text.Should().Be(BodyText);
        var warning = logs.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Values["UnexpectedTypes"].Should().Be("server_tool_use, some_future_block");
        warning.Message.Should().NotContain(BodyText);
        counts.Should().Equal(new Dictionary<string, long>
        {
            [AnthropicContentBlockMetrics.TypeThinking] = 1,
            [AnthropicContentBlockMetrics.TypeOther] = 2,
        });
    }

    // T-41: thinking だけでも、本文が 1 個も無ければ Warning（本文が失われたことに気づけるように）。
    [Fact]
    public async Task 本文が無いときは想定内の型でもWarningを出す()
    {
        var (logs, counts, text) = await CompleteAsync(Envelope(ThinkingBlock));

        text.Should().BeEmpty();
        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        counts.Should().Equal(new Dictionary<string, long> { [AnthropicContentBlockMetrics.TypeThinking] = 1 });
    }

    // T-41: 本文だけの応答はログも計器も出さない。
    [Fact]
    public async Task 本文だけの応答はログも計器も出さない()
    {
        var (logs, counts, _) = await CompleteAsync(Envelope(TextBlock));

        logs.Should().BeEmpty();
        counts.Should().BeEmpty();
    }

    // T-41: 計器の属性値は 3 値に閉じる（型名は上流が決める非有界な値）。
    [Theory]
    [InlineData("thinking", "thinking")]
    [InlineData("redacted_thinking", "redacted_thinking")]
    [InlineData("server_tool_use", "other")]
    [InlineData("(型不明)", "other")]
    [InlineData("Thinking", "other")]
    public void 計器の型属性は3値に閉じる(string type, string expected)
    {
        AnthropicContentBlockMetrics.Normalize(type).Should().Be(expected);
    }
}
