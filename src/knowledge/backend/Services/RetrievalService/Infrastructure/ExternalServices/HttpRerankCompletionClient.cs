using System.Net.Http.Json;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, FR-11, NFR-09, ADR-0010, ADR-0084 決定 1, [[IADR-0424]], [[IADR-0498]] 決定 5 (#1746 段 S2):
// 再順位付けのテキスト生成の **REST 輸送**（`POST /complete`）。並走中の正はこちら。
// s2s トークンは登録側（`Program.cs` の `AddLlmGatewayServiceToken`）が付ける（受け口は `ServiceCaller` を要する）。
//
// 🔴 **失敗は例外のまま上げる**（非 2xx は `EnsureSuccessStatusCode`、本文が読めなければ `InvalidOperationException`）。
// 縮退（元の順で返す）を決めるのは段（`ClaudeSearchReranker`）であり、輸送は別の送信先へ倒す枝を持たない。
public sealed class HttpRerankCompletionClient(HttpClient http) : IRerankCompletionClient
{
    public const string HttpClientName = "LlmGatewayRerank";

    public async Task<CompletionApiResponse> CompleteAsync(CompletionApiRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/complete", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CompletionApiResponse>(ct)
            ?? throw new InvalidOperationException("LLM gateway returned an empty completion body");
    }
}
