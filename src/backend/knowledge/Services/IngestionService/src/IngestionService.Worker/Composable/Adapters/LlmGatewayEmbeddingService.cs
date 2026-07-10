using IngestionService.Worker.Foundation.Ports;
using IngestionService.Worker.Foundation.Domain;
using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace IngestionService.Worker.Composable.Adapters;

// ADR-0013, ADR-0016: LLM ゲートウェイ経由で埋め込みを生成する（取り込み経路 = Purpose=Index）。
// 文書の機密区分（confidentiality）を渡し、ゲートウェイが越境判定（fail-closed）とモデル別コレクションを決める。
public class LlmGatewayEmbeddingService(HttpClient http) : IEmbeddingService
{
    public async Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "/embed",
            new EmbedApiRequest(text, confidentiality, EmbedPurpose.Index),
            ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(ct);

        // 応答欠落（予期しない空応答）は一時的な異常とみなし Retryable=true で再試行に回す（恒久スキップにしない）。
        if (result is null)
            return new EmbeddingResult([], string.Empty, false, Retryable: true);

        return new EmbeddingResult(result.Vector, result.Collection, result.Embedded, result.Retryable);
    }
}
