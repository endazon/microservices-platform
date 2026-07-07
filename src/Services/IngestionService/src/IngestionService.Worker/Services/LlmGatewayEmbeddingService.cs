using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace IngestionService.Worker.Services;

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

        // 応答欠落は安全側で fail-closed（索引しない）扱いにする。
        if (result is null)
            return new EmbeddingResult([], string.Empty, false);

        return new EmbeddingResult(result.Vector, result.Collection, result.Embedded);
    }
}
