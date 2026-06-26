using RetrievalService.Api.Abstractions;
using System.Net.Http.Json;

namespace RetrievalService.Api.Infrastructure;

// ADR-0013: LLM ゲートウェイ経由で埋め込みを生成
public class LlmGatewayEmbeddingService(HttpClient http) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync("/embed", new { text }, ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EmbedResponse>(ct);
        return result?.Vector ?? [];
    }

    private record EmbedResponse(float[] Vector);
}
