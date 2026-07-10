using RetrievalService.Api.Foundation.Ports;
using System.Net.Http.Json;
using KnowledgePlatform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Composable.Adapters;

// ADR-0013, ADR-0016: LLM ゲートウェイ経由でクエリ埋め込みを生成する（検索経路 = Purpose=Query）。
// クエリは検索対象コレクション（既定 voyage / 1024 次元）へ整合させるため、ゲートウェイは既定外部経路へ固定する。
// 高機密（ruri / 768 次元）コレクションの横断検索は FR-03 の後続課題。
public class LlmGatewayEmbeddingService(HttpClient http) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync(
            "/embed",
            new EmbedApiRequest(text, Confidentiality: null, Purpose: EmbedPurpose.Query),
            ct);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<EmbedApiResponse>(ct);
        return result?.Vector ?? [];
    }
}
