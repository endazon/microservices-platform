using System.Net;
using System.Text;
using FluentAssertions;
using LlmGateway.Api.Composable.Adapters;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmGateway.Api.Tests;

// FR-04, FR-11, ADR-0010, IADR-0114 (#290): Anthropic Messages API の応答から、SDK が解釈できない
// content ブロック型（thinking / redacted_thinking / server_tool_use / 将来型）を落として
// 「未知型 1 個で応答全体が解析不能になる」ことを防ぐサニタイザの検証。
//
// 実測（Anthropic.SDK 4.0.0）: content に thinking ブロックが 1 個でも含まれると
// `System.Text.Json.JsonException: Unknown type thinking` で応答全体が失われる。既知は
// text / image / tool_use / tool_result の 4 種のみ（本テストの許可リストの根拠）。
public class AnthropicContentBlockSanitizerTests
{
    private static string Envelope(string blocks) =>
        "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-5\","
        + "\"content\":[" + blocks + "],\"stop_reason\":\"end_turn\","
        + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";

    private const string TextBlock = """{"type":"text","text":"本文"}""";
    private const string ThinkingBlock = """{"type":"thinking","thinking":"","signature":"s"}""";

    // 未知型（thinking）を落とし、既知型だけを残す。
    [Fact]
    public void TrySanitize_ThinkingBlock_IsDroppedAndTextSurvives()
    {
        var sanitized = AnthropicContentBlockSanitizer.TrySanitize(
            Envelope($"{ThinkingBlock},{TextBlock}"), out var result, out var dropped);

        sanitized.Should().BeTrue();
        dropped.Should().Equal("thinking");
        result.Should().Contain("本文").And.NotContain("thinking");
    }

    // 将来追加される未知型でも同じ経路で落ちる（fail-safe の本体。型名の列挙に依存しない）。
    [Theory]
    [InlineData("redacted_thinking", """{"type":"redacted_thinking","data":"enc"}""")]
    [InlineData("server_tool_use", """{"type":"server_tool_use","id":"s1","name":"web_search","input":{}}""")]
    [InlineData("some_future_block", """{"type":"some_future_block","payload":{"deep":[1,2,3]}}""")]
    public void TrySanitize_UnknownFutureBlock_IsDropped(string type, string block)
    {
        var sanitized = AnthropicContentBlockSanitizer.TrySanitize(
            Envelope($"{block},{TextBlock}"), out var result, out var dropped);

        sanitized.Should().BeTrue();
        dropped.Should().Equal(type);
        result.Should().Contain("本文");
    }

    // 既知型はすべて温存する（ツール結果・画像を巻き添えで落とさないことの回帰）。
    [Fact]
    public void TrySanitize_AllKnownBlocks_IsNotRewritten()
    {
        var body = Envelope(string.Join(',',
            TextBlock,
            """{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="}}""",
            """{"type":"tool_use","id":"toolu_1","name":"t","input":{"a":1}}""",
            """{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"}"""));

        var sanitized = AnthropicContentBlockSanitizer.TrySanitize(body, out _, out var dropped);

        // 書き換えない＝素通し。既知型しか無い応答の本文は 1 バイトも触らない。
        sanitized.Should().BeFalse();
        dropped.Should().BeEmpty();
    }

    // 全ブロックが未知でも例外にせず、content 空として安全側へ縮退する（呼び出し側は空応答として扱う）。
    [Fact]
    public void TrySanitize_AllBlocksUnknown_YieldsEmptyContent()
    {
        var sanitized = AnthropicContentBlockSanitizer.TrySanitize(
            Envelope(ThinkingBlock), out var result, out var dropped);

        sanitized.Should().BeTrue();
        dropped.Should().Equal("thinking");
        result.Should().Contain("\"content\":[]");
    }

    // content を持たない JSON（エラー応答など）は対象外＝素通し。
    [Fact]
    public void TrySanitize_WithoutContentArray_IsNotRewritten()
    {
        var body = """{"type":"error","error":{"type":"overloaded_error","message":"overloaded"}}""";

        AnthropicContentBlockSanitizer.TrySanitize(body, out _, out _).Should().BeFalse();
    }

    // JSON として解釈できない本文は触らない（壊れた本文をさらに壊さない）。
    [Fact]
    public void TrySanitize_InvalidJson_IsNotRewritten()
    {
        AnthropicContentBlockSanitizer.TrySanitize("not json", out _, out _).Should().BeFalse();
    }

    // ハンドラは JSON 応答だけを対象にする。SSE（text/event-stream）は実測で SDK が thinking を
    // 素通しできるため触らない（ストリームを丸ごとバッファリングしないためでもある）。
    [Fact]
    public async Task Handler_EventStreamResponse_IsPassedThroughUnchanged()
    {
        const string sse = "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\"}}\n\n";
        var response = await SendAsync(sse, "text/event-stream", HttpStatusCode.OK);

        (await response.Content.ReadAsStringAsync()).Should().Be(sse);
    }

    // 非 2xx（API エラー）は SDK 側の例外整形に委ねるため触らない。
    [Fact]
    public async Task Handler_NonSuccessResponse_IsPassedThroughUnchanged()
    {
        var body = Envelope($"{ThinkingBlock},{TextBlock}");
        var response = await SendAsync(body, "application/json", HttpStatusCode.TooManyRequests);

        (await response.Content.ReadAsStringAsync()).Should().Be(body);
    }

    // JSON 応答は書き換わり、Content-Type は保持される（SDK が媒体型で分岐しても壊さない）。
    [Fact]
    public async Task Handler_JsonResponseWithUnknownBlock_IsRewritten()
    {
        var response = await SendAsync(Envelope($"{ThinkingBlock},{TextBlock}"), "application/json", HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("thinking").And.Contain("本文");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    private static async Task<HttpResponseMessage> SendAsync(string body, string mediaType, HttpStatusCode status)
    {
        var handler = new AnthropicResponseSanitizingHandler(NullLogger<AnthropicResponseSanitizingHandler>.Instance)
        {
            InnerHandler = new StubHandler(body, mediaType, status),
        };
        using var client = new HttpClient(handler);
        return await client.GetAsync("https://api.anthropic.test/v1/messages");
    }

    private sealed class StubHandler(string body, string mediaType, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            });
    }
}
