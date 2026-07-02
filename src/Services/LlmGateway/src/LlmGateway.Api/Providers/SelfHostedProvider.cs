using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Providers;

// FR-11, ADR-0010: セルフホスト（ティアA）LLM プロバイダ。
// OpenAI 互換の /chat/completions を持つ社内 GPU 基盤（vLLM 等）を呼ぶ想定。
// ADR-0010 のとおり「後付け可能」とし、既定では無効エンドポイントとして扱う（BaseUrl 未設定時は利用不可）。
public sealed class SelfHostedProvider(IHttpClientFactory httpFactory, IConfiguration config) : ILlmProvider
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

        var text = payload?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return new CompletionResult(text, payload?.Usage?.PromptTokens ?? 0, payload?.Usage?.CompletionTokens ?? 0);
    }

    // 埋め込みは別経路（FR-03 / ADR-0013）。本プロバイダでは未対応。
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    private sealed record OpenAiCompletionResponse(List<OpenAiChoice>? Choices, OpenAiUsage? Usage);
    private sealed record OpenAiChoice(OpenAiMessage? Message);
    private sealed record OpenAiMessage(string? Content);
    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
