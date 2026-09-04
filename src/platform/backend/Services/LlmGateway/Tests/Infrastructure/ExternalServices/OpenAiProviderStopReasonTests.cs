using System.Net;
using System.Text;
using AwesomeAssertions;
using LlmGateway.Infrastructure.ExternalServices;
using LlmGateway.Domain.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Shared.Contracts.Dtos;

namespace LlmGateway.Tests.Infrastructure.ExternalServices;

// T-20, FR-11, IADR-0104 / IADR-0109 (#394): OpenAI 互換プロバイダ（ティアA セルフホスト／
// ティアC Copilot）が finish_reason を読み、正準語彙の StopReason として返すことを固定する。
// 以前は finish_reason を読んでおらず StopReason が常に null で、拒否・上限到達・正常終了を
// 呼び出し側が区別できなかった（Claude 経路だけ区別できる非対称）。
[Trait("TestKind", "Unit")]
public class OpenAiProviderStopReasonTests
{
    // OpenAI 互換 /chat/completions の応答（必要部分）。finishReason=null は当該フィールドを載せない。
    private static string ChatCompletionJson(string? finishReason, string content)
    {
        var finish = finishReason is null ? "null" : $"\"{finishReason}\"";
        return $$"""
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "model": "oss-llm",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "{{content}}" },
              "finish_reason": {{finish}} }
          ],
          "usage": { "prompt_tokens": 11, "completion_tokens": 22 }
        }
        """;
    }

    // T-20d: セルフホスト（ティアA）の代表値。
    [Theory]
    [InlineData("stop", CompletionStopReasons.EndTurn)]
    [InlineData("length", CompletionStopReasons.MaxTokens)]
    [InlineData("content_filter", CompletionStopReasons.Refusal)]
    public async Task SelfHosted_CompleteAsync_MapsFinishReasonToStopReason(string finishReason, string expected)
    {
        var provider = SelfHosted(ChatCompletionJson(finishReason, "本文"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        result.StopReason.Should().Be(expected);
    }

    // T-20e: Copilot（ティアC）も同じ写像である（プロバイダ間で契約の意味が変わらないこと）。
    [Theory]
    [InlineData("stop", CompletionStopReasons.EndTurn)]
    [InlineData("length", CompletionStopReasons.MaxTokens)]
    [InlineData("content_filter", CompletionStopReasons.Refusal)]
    public async Task Copilot_CompleteAsync_MapsFinishReasonToStopReason(string finishReason, string expected)
    {
        var provider = Copilot(ChatCompletionJson(finishReason, "本文"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        result.StopReason.Should().Be(expected);
    }

    // T-20f: content_filter（拒否相当）は本文を破棄する。IADR-0104 の refusal と一貫させないと、
    // 拒否された断片を根拠に下流（AST 取引判断）が判断を進め fail-safe が破れる。
    [Fact]
    public async Task SelfHosted_WhenContentFilter_DiscardsBody()
    {
        var provider = SelfHosted(ChatCompletionJson("content_filter", "途中まで書きかけ"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("危険な要求"), TestContext.Current.CancellationToken);

        CompletionStopReasons.IsRefusal(result.StopReason).Should().BeTrue();
        result.Text.Should().BeEmpty();
        result.Text.Should().NotContain("途中まで書きかけ");
    }

    [Fact]
    public async Task Copilot_WhenContentFilter_DiscardsBody()
    {
        var provider = Copilot(ChatCompletionJson("content_filter", "途中まで書きかけ"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("危険な要求"), TestContext.Current.CancellationToken);

        CompletionStopReasons.IsRefusal(result.StopReason).Should().BeTrue();
        result.Text.Should().BeEmpty();
    }

    // T-20g: length（上限到達）の途中結果は破棄しない（IADR-0101 が記録した劣化の観測対象）。
    [Fact]
    public async Task SelfHosted_WhenLength_KeepsTruncatedBody()
    {
        var provider = SelfHosted(ChatCompletionJson("length", "途中まで"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("長文の要約"), TestContext.Current.CancellationToken);

        result.StopReason.Should().Be(CompletionStopReasons.MaxTokens);
        result.Text.Should().Be("途中まで");
    }

    // T-20b: 未知値は既定値へ潰さず原文のまま透過する。
    [Fact]
    public async Task SelfHosted_WhenUnknownFinishReason_PassesItThrough()
    {
        var provider = SelfHosted(ChatCompletionJson("guardrail_triggered", "本文"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        result.StopReason.Should().Be("guardrail_triggered");
        result.Text.Should().Be("本文");   // 未知理由では本文を破棄しない（refusal 相当のみ破棄）
    }

    // T-20h: 未知値は warn ログに残る（どのエンドポイントで未知語彙が来たかを追えるようにする）。
    [Fact]
    public async Task SelfHosted_WhenUnknownFinishReason_LogsWarning()
    {
        var provider = SelfHosted(ChatCompletionJson("guardrail_triggered", "本文"), out var logs);

        await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("guardrail_triggered");
    }

    // T-20c: finish_reason 欠落は null のまま（未対応プロバイダと同じ状態）。正常系のログを汚さない。
    [Fact]
    public async Task SelfHosted_WhenFinishReasonMissing_LeavesStopReasonNullWithoutWarning()
    {
        var provider = SelfHosted(ChatCompletionJson(null, "本文"), out var logs);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        result.StopReason.Should().BeNull();
        result.Text.Should().Be("本文");
        logs.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    // T-20i: ストリーミング（ILlmProvider の既定実装＝単一チャンクへ縮退）でも最終チャンクへ
    // 写像後の StopReason が載る（IADR-0037）。個別実装を持たないことの確認でもある。
    [Fact]
    public async Task SelfHosted_StreamAsync_FinalChunkCarriesMappedStopReason()
    {
        // StreamAsync は ILlmProvider の既定実装（default interface method）のため、
        // 具象型ではなくインターフェイス越しに呼ぶ。個別実装を持たないことの確認でもある。
        ILlmProvider provider = SelfHosted(ChatCompletionJson("content_filter", "書きかけ"), out _);

        var chunks = new List<CompletionChunk>();
        await foreach (var chunk in provider.StreamAsync(new CompletionRequest("危険な要求"), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var done = chunks.Should().ContainSingle(c => c.Done).Subject;
        done.StopReason.Should().Be(CompletionStopReasons.Refusal);
    }

    // 通常のトークン数・本文の扱いが回帰していないこと。
    [Fact]
    public async Task SelfHosted_WhenStop_ReturnsBodyAndUsage()
    {
        var provider = SelfHosted(ChatCompletionJson("stop", "回答本文"), out _);

        var result = await provider.CompleteAsync(new CompletionRequest("要約して"), TestContext.Current.CancellationToken);

        result.Text.Should().Be("回答本文");
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(22);
    }

    private static SelfHostedProvider SelfHosted(string body, out RecordingLogger<SelfHostedProvider> logs)
    {
        logs = new RecordingLogger<SelfHostedProvider>();
        return new SelfHostedProvider(
            new StubHttpClientFactory(body),
            Config("Llm:SelfHosted:BaseUrl"),
            logs);
    }

    private static CopilotProvider Copilot(string body, out RecordingLogger<CopilotProvider> logs)
    {
        logs = new RecordingLogger<CopilotProvider>();
        return new CopilotProvider(
            new StubHttpClientFactory(body),
            Config("Llm:Copilot:BaseUrl"),
            logs);
    }

    private static IConfiguration Config(string baseUrlKey)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [baseUrlKey] = "http://localhost:9999" })
            .Build();

    private sealed class StubHttpClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixedResponseHandler(body));
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    // ログ出力を検証するための最小のロガー（未知語彙が記録されることの固定に使う）。
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
