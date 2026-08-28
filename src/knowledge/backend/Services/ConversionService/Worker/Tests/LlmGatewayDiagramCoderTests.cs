using ConversionService.Worker.Infrastructure.ExternalServices;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ConversionService.Worker.Domain.Ports;
using ConversionService.Worker.Domain;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Worker.Tests;

// FR-12, ADR-0012/0010: 図コード化クライアント（LLMゲートウェイ /complete 経由）の単体テスト。
public class LlmGatewayDiagramCoderTests
{
    private static ExtractedFigure Figure() => new("fig-1", "image/png", [1, 2, 3]);

    private static LlmGatewayDiagramCoder Coder(CompletionApiResponse response)
    {
        var http = new HttpClient(new StubHandler(response))
        {
            BaseAddress = new Uri("http://llm-gateway:5007")
        };
        return new LlmGatewayDiagramCoder(http, NullLogger<LlmGatewayDiagramCoder>.Instance);
    }

    // Mermaid のフェンス付きコードが返れば、言語とコードを抽出して成功とする。
    [Fact]
    public async Task Codes_diagram_from_fenced_mermaid()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "```mermaid\ngraph TD; A-->B\n```", Model: "m", InputTokens: 1, OutputTokens: 1));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeTrue();
        result.Language.Should().Be("mermaid");
        result.Code.Should().Be("graph TD; A-->B");
    }

    // 機密区分で送信拒否（Sent=false）なら、画像として保持する（コード化しない）。
    [Fact]
    public async Task Retains_when_egress_denied()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "送信できません", Model: "", InputTokens: 0, OutputTokens: 0,
            Sent: false, Endpoint: null, RoutingReason: "restricted-blocked"));

        var result = await coder.CodeAsync(Figure(), "restricted", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Contain("egress-denied");
    }

    // T-16, IADR-0104 (#379): モデルが拒否（stopReason="refusal"）した場合も画像として保持するが、
    // 理由は「コード化不能」ではなく拒否として記録する。拒否は sent=true・本文空で返るため、
    // sent とフェンスの有無だけを見ると「図をコード化できなかった」と誤って記録される。
    [Fact]
    public async Task Retains_with_refusal_reason_when_model_refuses()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "", Model: "claude-haiku-4-5", InputTokens: 1, OutputTokens: 0,
            Sent: true, Endpoint: "claude-managed", RoutingReason: "ok",
            StopReason: CompletionStopReasons.Refusal));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();          // 画像保持（fail-safe は不変）
        result.Reason.Should().Be("llm-refused");
        result.Reason.Should().NotBe("not-codeable");
    }

    // コード化不能（「不可」等、フェンスなし）なら画像として保持する。
    [Fact]
    public async Task Retains_when_not_codeable()
    {
        var coder = Coder(new CompletionApiResponse(
            Text: "不可", Model: "m", InputTokens: 1, OutputTokens: 1));

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("not-codeable");
    }

    // FR-11: 送信 purpose は "diagram-coding"（LlmGateway の PurposeModels 設定キーと一致）であること。
    // このキーが不一致だと用途別モデル（haiku）選択が発火せず既定モデルへ縮退する（Issue #58 の #1）。
    [Fact]
    public async Task Sends_purpose_diagram_coding()
    {
        var capture = new CapturingHandler(new CompletionApiResponse(
            Text: "```mermaid\ngraph TD; A-->B\n```", Model: "m", InputTokens: 1, OutputTokens: 1));
        var http = new HttpClient(capture) { BaseAddress = new Uri("http://llm-gateway:5007") };
        var coder = new LlmGatewayDiagramCoder(http, NullLogger<LlmGatewayDiagramCoder>.Instance);

        await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        capture.Request.Should().NotBeNull();
        capture.Request!.Purpose.Should().Be("diagram-coding");
        capture.Request.Confidentiality.Should().Be("internal");
    }

    // 呼び出し失敗（例外）も画像保持へ縮退し、例外を送出しない（変換を止めない）。
    [Fact]
    public async Task Retains_when_call_fails()
    {
        var http = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://llm-gateway:5007")
        };
        var coder = new LlmGatewayDiagramCoder(http, NullLogger<LlmGatewayDiagramCoder>.Instance);

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("llm-call-failed");
    }

    private sealed class StubHandler(CompletionApiResponse response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(response);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    // 送信リクエスト本文を捕捉しつつ、定型応答を返すハンドラ。
    private sealed class CapturingHandler(CompletionApiResponse response) : HttpMessageHandler
    {
        public CompletionApiRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = await request.Content!.ReadFromJsonAsync<CompletionApiRequest>(cancellationToken);
            var json = JsonSerializer.Serialize(response);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
