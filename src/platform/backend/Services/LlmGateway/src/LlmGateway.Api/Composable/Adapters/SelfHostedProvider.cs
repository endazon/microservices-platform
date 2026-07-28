using LlmGateway.Api.Foundation.Ports;
using Platform.Shared.Contracts.Dtos;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Composable.Adapters;

// FR-11, ADR-0010: セルフホスト（ティアA）LLM プロバイダ。
// OpenAI 互換の /chat/completions を持つ社内 GPU 基盤（vLLM 等）を呼ぶ想定。
// ADR-0010 のとおり「後付け可能」とし、既定では無効エンドポイントとして扱う（BaseUrl 未設定時は利用不可）。
// IADR-0109 (#394): 応答の finish_reason を契約の正準語彙（CompletionStopReasons）へ正規化する
// （IADR-0104 の StopReason をティアA 経路でも意味あるものにする）。
public sealed class SelfHostedProvider(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    ILogger<SelfHostedProvider> logger) : ILlmProvider
{
    private readonly string _baseUrl = config["Llm:SelfHosted:BaseUrl"] ?? string.Empty;
    private readonly string _defaultModel = config["Llm:SelfHosted:Model"] ?? "oss-llm";

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("セルフホスト LLM の BaseUrl が未設定です（Llm:SelfHosted:BaseUrl）。");

        var client = httpFactory.CreateClient("SelfHostedLlm");
        client.BaseAddress = new Uri(_baseUrl);

        var body = new
        {
            model = request.Model ?? _defaultModel,
            max_tokens = request.MaxTokens,
            messages = new[] { new { role = "user", content = request.Prompt } }
        };

        var resp = await client.PostAsJsonAsync("/v1/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<OpenAiCompletionResponse>(ct);

        var choice = payload?.Choices?.FirstOrDefault();

        // IADR-0109 (#394), IADR-0104: content を読む前に終了理由を確定させる。
        var (stopReason, unmapped) = OpenAiFinishReasons.Map(choice?.FinishReason);
        if (unmapped)
            logger.LogWarning(
                "Unmapped OpenAI finish_reason \"{FinishReason}\" from self-hosted endpoint (model {Model}). " +
                "Passing it through verbatim; callers only branch on the canonical vocabulary.",
                choice!.FinishReason, request.Model ?? _defaultModel);

        // IADR-0104: 拒否（content_filter → refusal）のときだけ本文を破棄する。安全性フィルタは本文の
        // 途中で停止し得るため、断片が非空のまま下流へ流れると（AST 取引判断は Text の非空を根拠に
        // 売買判断へ進む）fail-safe が破れる。length → max_tokens の途中結果は正当な観測対象であり破棄しない。
        var text = CompletionStopReasons.IsRefusal(stopReason)
            ? string.Empty
            : choice?.Message?.Content ?? string.Empty;

        return new CompletionResult(
            text, payload?.Usage?.PromptTokens ?? 0, payload?.Usage?.CompletionTokens ?? 0, stopReason);
    }

    private sealed record OpenAiCompletionResponse(List<OpenAiChoice>? Choices, OpenAiUsage? Usage);
    private sealed record OpenAiChoice(
        OpenAiMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
    private sealed record OpenAiMessage(string? Content);
    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
