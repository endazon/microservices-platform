using LlmGateway.Api.Foundation.Ports;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LlmGateway.Api.Composable.Adapters;

// FR-02, ADR-0016: Voyage AI 埋め込みプロバイダ（既定・ティアB=保護契約済み外部API）。
// voyage-3.5（次元 1024）を既定とする。API キーは Secret 経由（Embedding:Voyage:ApiKey、既定空）。
// 契約時にゼロ保持（学習利用オプトアウト）を設定する（運用仕様書に記録）。BaseUrl/ApiKey 未設定時は
// 呼び出さず例外を投げ、エンドポイント側で fail-closed（Embedded=false）へ縮退させる。
public sealed class VoyageEmbeddingProvider(IHttpClientFactory httpFactory, IConfiguration config)
    : IEmbeddingProvider
{
    private readonly string _baseUrl = config["Embedding:Voyage:BaseUrl"] ?? "https://api.voyageai.com";
    private readonly string _apiKey = config["Embedding:Voyage:ApiKey"] ?? string.Empty;

    public async Task<float[]> EmbedAsync(string text, string model, int dimensions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Voyage AI の API キーが未設定です（Embedding:Voyage:ApiKey）。");

        var client = httpFactory.CreateClient("VoyageEmbedding");
        client.BaseAddress = new Uri(_baseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // Voyage AI /v1/embeddings: input（配列）, model, output_dimension で次元を明示する。
        var body = new
        {
            input = new[] { text },
            model,
            output_dimension = dimensions
        };

        var resp = await client.PostAsJsonAsync("/v1/embeddings", body, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<VoyageEmbeddingResponse>(ct);

        return payload?.Data?.FirstOrDefault()?.Embedding ?? [];
    }

    private sealed record VoyageEmbeddingResponse(
        [property: JsonPropertyName("data")] List<VoyageEmbeddingItem>? Data);

    private sealed record VoyageEmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
