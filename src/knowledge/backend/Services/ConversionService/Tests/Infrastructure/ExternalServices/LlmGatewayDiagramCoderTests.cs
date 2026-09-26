using ConversionService.Infrastructure.ExternalServices;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ConversionService.Domain.Ports;
using ConversionService.Domain;
using AwesomeAssertions;
using Platform.Shared.Contracts.Dtos;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConversionService.Tests.Infrastructure.ExternalServices;

// FR-12, ADR-0012/0010: 図コード化クライアント（LLMゲートウェイ /complete 経由）の単体テスト。
[Trait("TestKind", "Unit")]
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

    // FR-12 テスト仕様 T-44 (#1621), UC-06 例外フロー「図コード化（LLM）の失敗は画像保持へ縮退」:
    // **LLM ゲートウェイの時間切れも呼び出し失敗である。** `HttpClient.Timeout` の経過は
    // `TaskCanceledException`（`OperationCanceledException` の派生）で表れ、呼び出し元の ct は立っていない。
    // 🔴 従前は型だけで絞っており（`ex is not OperationCanceledException`）、時間切れ 1 回で正規化全体が失敗していた。
    [Fact]
    public async Task Retains_when_gateway_times_out()
    {
        var http = new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri("http://llm-gateway:5007"),
            Timeout = TimeSpan.FromMilliseconds(100),
        };
        var coder = new LlmGatewayDiagramCoder(http, NullLogger<LlmGatewayDiagramCoder>.Instance);

        var result = await coder.CodeAsync(Figure(), "internal", TestContext.Current.CancellationToken);

        result.Coded.Should().BeFalse();
        result.Reason.Should().Be("llm-call-failed");
    }

    // FR-12 T-44 の器の確認: 上の試験が注入しているのは**本物の時間切れの形**である
    // （`TaskCanceledException`・内側に `TimeoutException`・呼び出し元の ct は立っていない）。
    // これが崩れると、上の試験は時間切れではない何かを畳んで緑になり得る。
    [Fact]
    public async Task Hanging_gateway_fixture_produces_the_timeout_shape()
    {
        using var http = new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri("http://llm-gateway:5007"),
            Timeout = TimeSpan.FromMilliseconds(100),
        };
        var ct = TestContext.Current.CancellationToken;

        var act = () => http.PostAsync("/complete", new StringContent("{}"), ct);

        var thrown = await act.Should().ThrowExactlyAsync<TaskCanceledException>();
        thrown.Which.InnerException.Should().BeOfType<TimeoutException>();
        ct.IsCancellationRequested.Should().BeFalse();
    }

    // FR-12 T-44 の対照 (#1621): **呼び出し元（メッセージ消費）の取り消しは畳まずに外へ出す。**
    // 要求の途中で呼び出し元の ct を取り消すと、`HttpClient` はその ct を運ぶ `TaskCanceledException` を投げる。
    // 画像保持へ畳むと、停止要求の最中に図を画像として保管し、変換を「成功」として記録してしまう。
    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var http = new HttpClient(new HangingHandler(onSend: cts.Cancel))
        {
            BaseAddress = new Uri("http://llm-gateway:5007"),
            // 取り消しが伝わらなかったときに 100 秒待たないための上限（本試験の期待はこれより先に投げること）。
            Timeout = TimeSpan.FromSeconds(30),
        };
        var coder = new LlmGatewayDiagramCoder(http, NullLogger<LlmGatewayDiagramCoder>.Instance);

        var act = () => coder.CodeAsync(Figure(), "internal", cts.Token);

        var thrown = await act.Should().ThrowAsync<TaskCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token);
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

    // 応答を返さず、要求の ct が立つまで待つ（LLM ゲートウェイが応答しない状態）。
    // `onSend` は要求が届いた時点で呼ぶ（呼び出し元の取り消しを「要求の途中」で起こすため）。
    private sealed class HangingHandler(Action? onSend = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }
}
