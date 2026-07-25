using System.Net;
using System.Text;
using Anthropic.SDK;
using FluentAssertions;
using LlmGateway.Api.Composable.Adapters;
using LlmGateway.Api.Foundation.Ports;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Configuration;

namespace LlmGateway.Api.Tests;

// FR-11, ADR-0025, IADR-0104 (#379): ClaudeProvider が Anthropic 応答の stop_reason を判別し、
// 「モデルが拒否した（refusal）」と「上限到達（max_tokens）」と「正常終了（end_turn）」を
// 呼び出し側が区別できることを固定する。refusal は HTTP 200・例外なしで到着するため、
// 判別しないと空文字へ静かに縮退し、監査ログ上「送信したが空応答」と見分けられなくなる。
public class ClaudeProviderStopReasonTests
{
    private static ClaudeProvider Provider(HttpMessageHandler handler) =>
        new(new AnthropicClient(new APIAuthentication("test-key"), new HttpClient(handler)),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Model"] = "claude-opus-5" })
                .Build());

    // Anthropic Messages API の応答 JSON（必要部分）。content は 0 個以上の text ブロック。
    private static string MessageJson(string stopReason, params string[] texts)
    {
        var content = string.Join(",", texts.Select(t => $$"""{"type":"text","text":"{{t}}"}"""));
        return $$"""
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-5",
          "content": [{{content}}],
          "stop_reason": "{{stopReason}}",
          "stop_sequence": null,
          "usage": { "input_tokens": 11, "output_tokens": 22 }
        }
        """;
    }

    // T-16: 拒否（本文なし）。stop_reason が伝わり、空応答と区別できる。
    [Fact]
    public async Task CompleteAsync_WhenRefusalWithoutBody_ReportsRefusalStopReason()
    {
        var provider = Provider(new StubHandler(MessageJson("refusal")));

        var result = await provider.CompleteAsync(new CompletionRequest("危険な要求"));

        result.StopReason.Should().Be(CompletionStopReasons.Refusal);
        result.Text.Should().BeEmpty();
        CompletionStopReasons.IsRefusal(result.StopReason).Should().BeTrue();
    }

    // T-16: 拒否（部分本文あり）。安全性分類器が本文の途中で停止した場合、断片を下流へ流さない
    // （AST 取引判断は Text の非空を根拠に売買判断へ進むため、断片を返すと fail-safe が破れる）。
    [Fact]
    public async Task CompleteAsync_WhenRefusalWithPartialBody_DiscardsBody()
    {
        var provider = Provider(new StubHandler(MessageJson("refusal", "途中まで書きかけ")));

        var result = await provider.CompleteAsync(new CompletionRequest("危険な要求"));

        result.StopReason.Should().Be(CompletionStopReasons.Refusal);
        result.Text.Should().BeEmpty();                       // 断片は破棄する
        result.Text.Should().NotContain("途中まで書きかけ");
    }

    // T-17: 上限到達。thinking が max_tokens を食い切ると本文が空になり得る（IADR-0101）。
    // 空文字である点は refusal と同じだが、stop_reason で切り分けられる。
    [Fact]
    public async Task CompleteAsync_WhenMaxTokens_IsDistinguishableFromRefusal()
    {
        var provider = Provider(new StubHandler(MessageJson("max_tokens")));

        var result = await provider.CompleteAsync(new CompletionRequest("長文の要約"));

        result.StopReason.Should().Be(CompletionStopReasons.MaxTokens);
        CompletionStopReasons.IsRefusal(result.StopReason).Should().BeFalse();
    }

    // T-17: 上限到達で本文が途中まで出た場合、その断片は破棄しない（正当な途中結果であり、
    // IADR-0101 が記録した「本文が途中で切れる」劣化の観測対象）。
    [Fact]
    public async Task CompleteAsync_WhenMaxTokens_KeepsTruncatedBody()
    {
        var provider = Provider(new StubHandler(MessageJson("max_tokens", "途中まで")));

        var result = await provider.CompleteAsync(new CompletionRequest("長文の要約"));

        result.StopReason.Should().Be(CompletionStopReasons.MaxTokens);
        result.Text.Should().Be("途中まで");
    }

    // T-18: 正常終了。本文・トークン数は従来どおりで、stop_reason が透過する（回帰防止）。
    [Fact]
    public async Task CompleteAsync_WhenEndTurn_ReturnsBodyAndStopReason()
    {
        var provider = Provider(new StubHandler(MessageJson("end_turn", "回答本文")));

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"));

        result.StopReason.Should().Be(CompletionStopReasons.EndTurn);
        result.Text.Should().Be("回答本文");
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(22);
    }

    // 未知の stop_reason は enum へ丸めず文字列のまま透過する（将来語彙が増えても黙って
    // 既定値へ落ちない。IADR-0104 §決定）。
    [Fact]
    public async Task CompleteAsync_WhenUnknownStopReason_PassesItThrough()
    {
        var provider = Provider(new StubHandler(MessageJson("some_future_reason", "本文")));

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"));

        result.StopReason.Should().Be("some_future_reason");
        result.Text.Should().Be("本文");   // 未知理由では本文を破棄しない（refusal のみ破棄）
    }

    // T-16: ストリーミングでも message_delta の stop_reason を最終チャンクへ載せる（IADR-0037）。
    // 送出済みデルタは撤回できないため、呼び出し側が done の理由を見て破棄できることが要件。
    [Fact]
    public async Task StreamAsync_WhenRefusal_FinalChunkCarriesRefusal()
    {
        const string sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":11,"output_tokens":0}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"書きかけ"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":22}}

        event: message_stop
        data: {"type":"message_stop"}


        """;
        var provider = Provider(new StubHandler(sse, "text/event-stream"));

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(new CompletionRequest("危険な要求")))
            chunks.Add(chunk);

        var done = chunks.Should().ContainSingle(c => c.Done).Subject;
        done.StopReason.Should().Be(CompletionStopReasons.Refusal);
    }

    // 正常終了のストリーミングでは本文デルタが流れ、最終チャンクに end_turn が載る（回帰防止）。
    [Fact]
    public async Task StreamAsync_WhenEndTurn_StreamsDeltasAndCarriesStopReason()
    {
        const string sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":11,"output_tokens":0}}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"回答"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":22}}

        event: message_stop
        data: {"type":"message_stop"}


        """;
        var provider = Provider(new StubHandler(sse, "text/event-stream"));

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(new CompletionRequest("要約して")))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.TextDelta == "回答");
        var done = chunks.Should().ContainSingle(c => c.Done).Subject;
        done.StopReason.Should().Be(CompletionStopReasons.EndTurn);
        done.InputTokens.Should().Be(11);
        done.OutputTokens.Should().Be(22);
    }

    // Anthropic への実送信を伴わないよう、固定応答を返すスタブハンドラ。
    private sealed class StubHandler(string body, string mediaType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }
}
