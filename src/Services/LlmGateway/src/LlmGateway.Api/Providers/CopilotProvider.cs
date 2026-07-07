using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Providers;

// FR-11, ADR-0010, IADR-0022: GitHub Copilot SDK 経由の LLM プロバイダ（最難関用途の別経路）。
// Copilot はモデルへ OpenAI 互換の /chat/completions でアクセスできるため、SelfHostedProvider と同じ
// HTTP トランスポートで実装する（専用 NuGet への依存を増やさない）。ベアラトークンを付与する点のみ異なる。
//
// 安全側の既定: 送信先ティア（08_data-egress-policy の契約条件＝ZDR/学習不使用/レジデンシー）が未確定のため、
// エンドポイント定義は「ティアC・既定 Enabled=false」で登録する（selfhosted と同じ後付けパターン）。
// BaseUrl/Token 未設定時は呼び出さず、確定後に設定で有効化する（IADR-0022 のフォローアップ）。
public sealed class CopilotProvider(IHttpClientFactory httpFactory, IConfiguration config) : ILlmProvider
{
    private readonly string _baseUrl = config["Llm:Copilot:BaseUrl"] ?? string.Empty;
    private readonly string _token = config["Llm:Copilot:Token"] ?? string.Empty;
    private readonly string _defaultModel = config["Llm:Copilot:Model"] ?? "gpt-5";

    public async Task<CompletionResult> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("GitHub Copilot の BaseUrl が未設定です（Llm:Copilot:BaseUrl）。");

        var client = httpFactory.CreateClient("CopilotLlm");
        client.BaseAddress = new Uri(_baseUrl);
        if (!string.IsNullOrWhiteSpace(_token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var body = new
        {
            model = request.Model ?? _defaultModel,
            max_tokens = request.MaxTokens,
            messages = new[] { new { role = "user", content = request.Prompt } }
        };

        var resp = await client.PostAsJsonAsync("/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<OpenAiCompletionResponse>(ct);

        var text = payload?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return new CompletionResult(text, payload?.Usage?.PromptTokens ?? 0, payload?.Usage?.CompletionTokens ?? 0);
    }

    private sealed record OpenAiCompletionResponse(List<OpenAiChoice>? Choices, OpenAiUsage? Usage);
    private sealed record OpenAiChoice(OpenAiMessage? Message);
    private sealed record OpenAiMessage(string? Content);
    private sealed record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
