using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.SDK;
using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using LlmGateway.Domain.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// FR-04, FR-11, ADR-0010, IADR-0114 (AST#290): thinking（拡張思考）ブロックを含む応答でも
// ClaudeProvider が本文を取り出せることの回帰テスト。
//
// 背景: 用途別の割当モデル（trade-decision / report-daily = claude-sonnet-5、report-weekly /
// report-monthly / analysis / default = claude-opus-5）はいずれも thinking が
// 既定で有効であり、応答の content には必ず thinking ブロックが載る。Anthropic.SDK 4.0.0 は
// 当該型を知らないため、サニタイズが無いと `JsonException: Unknown type thinking` で
// **応答全体**が失われ、/complete は Sent=false へ縮退する（＝AI 自律取引が成立しない）。
//
// ［2026-08-18 追記 / #850］計画 ADR-0038 決定 1 により analysis の割当を claude-fable-5 → claude-opus-5 へ
// 改めたので、上の背景記述を現行値へ書き改めた。**本テストの挙動は変わらない** —— ここは背景の説明であって
// テストが渡すモデル文字列ではない（本ファイルが実際に渡すのは claude-sonnet-5 と null だけである）。
public class ClaudeProviderThinkingTests
{
    private static string Envelope(string blocks, string stopReason = "end_turn") =>
        "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\","
        + "\"content\":[" + blocks + "],\"stop_reason\":\"" + stopReason + "\","
        + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

    private const string ThinkingBlock = """{"type":"thinking","thinking":"","signature":"s"}""";

    private static string TextBlock(string text) =>
        "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}";

    // (1) 例外を投げない／(2) 本文テキストを正しく取り出す。
    [Fact]
    public async Task CompleteAsync_WithThinkingBlock_ReturnsTextInsteadOfThrowing()
    {
        var provider = CreateProvider(Envelope($"{ThinkingBlock},{TextBlock("要約本文")}"), sanitize: true);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して", 4096, "claude-sonnet-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be("要約本文");
        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(5);
        result.StopReason.Should().Be("end_turn");
    }

    // (3) 取引判断の構造化出力（JSON 本文）が Buy / Sell / Hold として正しく取り出せる。
    // AST 側の TradeDecisionParser は本文から JSON を抽出して action を読むため、
    // 本文が原文のまま届くことがそのまま「判断が成立する」ことの前提になる。
    [Theory]
    [InlineData("Buy")]
    [InlineData("Sell")]
    [InlineData("Hold")]
    public async Task CompleteAsync_WithThinkingBlock_PreservesStructuredDecisionBody(string action)
    {
        var body = $$"""{"action":"{{action}}","rationale":"根拠","referencePrice":190.5,"stopLossDistancePerShare":5.5}""";
        var provider = CreateProvider(Envelope($"{ThinkingBlock},{TextBlock(body)}"), sanitize: true);

        var result = await provider.CompleteAsync(new CompletionRequest("判断して", 4096, "claude-sonnet-5"), TestContext.Current.CancellationToken);

        result.Text.Should().Be(body);
        JsonSerializer.Deserialize<JsonElement>(result.Text)
            .GetProperty("action").GetString().Should().Be(action);
    }

    // 未知型が将来増えても同じ経路で救われる（型名の列挙に依存しないことの回帰）。
    [Fact]
    public async Task CompleteAsync_WithUnknownFutureBlock_ReturnsText()
    {
        var provider = CreateProvider(
            Envelope($$"""{"type":"some_future_block","payload":1},{{TextBlock("本文")}}"""), sanitize: true);

        (await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken)).Text.Should().Be("本文");
    }

    // 変異テスト: サニタイズを外すと同じ応答で例外になる（ガードが load-bearing であることの実証）。
    [Fact]
    public async Task CompleteAsync_WithoutSanitizer_ThrowsUnknownTypeThinking()
    {
        var provider = CreateProvider(Envelope($"{ThinkingBlock},{TextBlock("要約本文")}"), sanitize: false);

        var act = () => provider.CompleteAsync(new CompletionRequest("要約して", 4096, "claude-sonnet-5"), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<JsonException>()).WithMessage("*Unknown type thinking*");
    }

    // 既知型だけの応答（サニタイズ不要）でも本文を取り出せる。ハンドラが判定のために本文を
    // 1 度読むため、書き換えない経路で SDK が本文を読み直せることの回帰（二重読み取り）。
    [Fact]
    public async Task CompleteAsync_WithKnownBlocksOnly_IsReadableAfterInspection()
    {
        var provider = CreateProvider(Envelope(TextBlock("素通し本文")), sanitize: true);

        (await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken)).Text.Should().Be("素通し本文");
    }

    // IADR-0104 の安全既定は不変: 拒否（refusal）は thinking の有無に関わらず本文を返さない。
    [Fact]
    public async Task CompleteAsync_RefusalWithThinkingBlock_StillReturnsEmptyText()
    {
        var provider = CreateProvider(
            Envelope($"{ThinkingBlock},{TextBlock("断片")}", stopReason: "refusal"), sanitize: true);

        var result = await provider.CompleteAsync(new CompletionRequest("q", 4096, null), TestContext.Current.CancellationToken);

        result.Text.Should().BeEmpty();
        result.StopReason.Should().Be("refusal");
    }

    private static ClaudeProvider CreateProvider(string responseBody, bool sanitize)
    {
        HttpMessageHandler handler = new StubHandler(responseBody);
        if (sanitize)
        {
            handler = new AnthropicResponseSanitizingHandler(
                NullLogger<AnthropicResponseSanitizingHandler>.Instance)
            {
                InnerHandler = handler,
            };
        }

        var client = new AnthropicClient(new APIAuthentication("test-key"), new HttpClient(handler));
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        return new ClaudeProvider(client, config);
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
