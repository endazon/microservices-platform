using System.Net.Http.Json;
using System.Text.Json;
using McpServer.Domain;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, ADR-0024 §2: 申告された `endpoint` へツール実行を委譲する既定実装。
// MCP サーバー自身はドメインの判断を持たない（認可判定も各サービスへ委譲する。§3）。
public sealed class HttpToolInvoker(IHttpClientFactory httpClientFactory) : IToolInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<McpToolResult> InvokeAsync(
        PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(nameof(HttpToolInvoker));
        var payload = new
        {
            scope,
            // 引数は MCP クライアントから来た JSON をそのまま渡す（解釈は下流サービスの責務）。
            arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(argumentsJson).RootElement
        };

        var response = await client.PostAsJsonAsync(tool.Declaration.Endpoint, payload, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<McpToolResult>(JsonOptions, ct)
            ?? new McpToolResult([], 0);
    }
}
