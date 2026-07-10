using LlmGateway.Api.Foundation.Ports;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Composable.Adapters;

// FR-02, FR-05, ADR-0016, ADR-0017: セルフホスト埋め込みプロバイダ（高機密・ティアA=社外送信なし）。
// confidential/restricted 文書の埋め込みをこの経路に固定する。モデルは Ruri v3（第一採用・次元 768 系、ADR-0017）。
// OpenAI 互換の /v1/embeddings を持つ社内基盤（TEI / vLLM 等）を呼ぶ想定。ADR-0010/0013 のとおり「後付け可能」とし、
// 既定では無効エンドポイントとして扱う（BaseUrl 未設定時は利用不可＝高機密は fail-closed で外部送信しない）。
public sealed class SelfHostedEmbeddingProvider(IHttpClientFactory httpFactory, IConfiguration config)
    : IEmbeddingProvider
{
    private readonly string _baseUrl = config["Embedding:SelfHosted:BaseUrl"] ?? string.Empty;

    public async Task<float[]> EmbedAsync(string text, string model, int dimensions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            throw new InvalidOperationException("セルフホスト埋め込みの BaseUrl が未設定です（Embedding:SelfHosted:BaseUrl）。");

        var client = httpFactory.CreateClient("SelfHostedEmbedding");
        client.BaseAddress = new Uri(_baseUrl);

        var body = new { input = text, model };

        var resp = await client.PostAsJsonAsync("/v1/embeddings", body, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(ct);

        return payload?.Data?.FirstOrDefault()?.Embedding ?? [];
    }

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("data")] List<OpenAiEmbeddingItem>? Data);

    private sealed record OpenAiEmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
