using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using AwesomeAssertions;
using LlmGateway.Common.Observability;
using LlmGateway.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-41, FR-11, NFR-19, ADR-0006, IADR-0535 (#1917): content ブロック除去のログ段階と除去件数の計器を固定する。
//
// 5.5 系は thinking を無効にできず（IADR-0531）、thinking の除去は毎応答の通常の形である。
// 1 応答 1 行の Warning（IADR-0114 決定 5 の当初の形）では PoC の 7 時間で 685 行になり、本物の警告が埋もれた。
//   - thinking / redacted_thinking だけを除去し既知ブロックが残る → Debug
//   - 想定外の型を除去した／除去で content が空になった       → Warning
//   - 件数は型を問わず llm.anthropic.content_blocks_dropped で数える
// ログ段階は RecordingLoggerFactory で Level を表明する（AnthropicHttpClientTests と同じ流儀）。
// 計器は Meter の**インスタンス**で購読する（[[IADR-0394]] 決定 1）。共有 Meter 名へ発行するので加入もする。
[Collection(SharedMeterCollection.Name)]
[Trait("TestKind", "Unit")]
public class AnthropicResponseSanitizingHandlerLogLevelTests
{
    private const string Category = "LlmGateway.Infrastructure.ExternalServices.AnthropicResponseSanitizingHandler";

    // 本文の文字列はログに載ってはならない（型名のみ。IADR-0114 決定 5）ので、ログの文言と紛れない値にする。
    private const string BodyText = "回答の実体XYZ";
    private const string TextBlock = """{"type":"text","text":"回答の実体XYZ"}""";
    private const string ThinkingBlock = """{"type":"thinking","thinking":"","signature":"s"}""";
    private const string RedactedThinkingBlock = """{"type":"redacted_thinking","data":"opaque"}""";
    private const string ServerToolUseBlock = """{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}""";

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

    private static async Task<(IReadOnlyList<RecordingLoggerFactory.Entry> Logs, IReadOnlyDictionary<string, long> Counts, string Body)>
        SendAsync(string body)
    {
        using var loggers = new RecordingLoggerFactory();
        var metrics = TestContentBlockMetrics.Create(out var meterFactory);
        using var probe = new Probe(meterFactory.Create(AnthropicContentBlockMetrics.MeterName));

        var handler = new AnthropicResponseSanitizingHandler(
            new Logger<AnthropicResponseSanitizingHandler>(loggers), metrics)
        {
            InnerHandler = new StubHandler(body),
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://api.anthropic.test/v1/messages", TestContext.Current.CancellationToken);
        var sanitized = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return (loggers.Entries.Where(e => e.Category == Category).ToList(), probe.CountsByType(), sanitized);
    }

    // T-41: thinking だけの除去は想定内（5.5 系の毎応答）。Debug 1 行・Warning 0 行、件数は計器へ。
    [Fact]
    public async Task Thinking除去で本文が残るときはDebugで出しWarningを出さない()
    {
        var (logs, counts, body) = await SendAsync(Envelope($"{ThinkingBlock},{TextBlock}"));

        body.Should().Contain(BodyText).And.NotContain("\"thinking\"");
        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        logs.Single().Message.Should().Contain("thinking");
        counts.Should().Equal(new Dictionary<string, long> { [AnthropicContentBlockMetrics.TypeThinking] = 1 });
    }

    // T-41: redacted_thinking も想定内。型ごとに数える。
    [Fact]
    public async Task RedactedThinkingも想定内として扱い型ごとに数える()
    {
        var (logs, counts, _) = await SendAsync(Envelope($"{RedactedThinkingBlock},{ThinkingBlock},{TextBlock}"));

        logs.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logs.Should().ContainSingle(e => e.Level == LogLevel.Debug);
        counts.Should().Equal(new Dictionary<string, long>
        {
            [AnthropicContentBlockMetrics.TypeThinking] = 1,
            [AnthropicContentBlockMetrics.TypeRedactedThinking] = 1,
        });
    }

    // T-41: 想定外の型（server_tool_use）は Warning。型名を構造化値に載せ、計器は other へ集約する。
    [Fact]
    public async Task 想定外の型を除去したときはWarningで型名を出す()
    {
        var (logs, counts, body) = await SendAsync(Envelope($"{ThinkingBlock},{ServerToolUseBlock},{TextBlock}"));

        body.Should().Contain(BodyText);
        var warning = logs.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Values["UnexpectedTypes"].Should().Be("server_tool_use");
        warning.Message.Should().NotContain(BodyText);
        counts.Should().Equal(new Dictionary<string, long>
        {
            [AnthropicContentBlockMetrics.TypeThinking] = 1,
            [AnthropicContentBlockMetrics.TypeOther] = 1,
        });
    }

    // T-41: thinking だけでも、除去で content が空になったら Warning（本文が失われたことに気づけるように）。
    [Fact]
    public async Task 除去でcontentが空になったときは想定内の型でもWarningを出す()
    {
        var (logs, counts, body) = await SendAsync(Envelope(ThinkingBlock));

        body.Should().Contain("\"content\":[]");
        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        counts.Should().Equal(new Dictionary<string, long> { [AnthropicContentBlockMetrics.TypeThinking] = 1 });
    }

    // T-41: 既知型だけの応答はログも計器も出さない（素通し。IADR-0114 決定 4）。
    [Fact]
    public async Task 既知型だけの応答はログも計器も出さない()
    {
        var (logs, counts, _) = await SendAsync(Envelope(TextBlock));

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

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
