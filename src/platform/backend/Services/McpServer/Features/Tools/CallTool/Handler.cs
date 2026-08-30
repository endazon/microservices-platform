using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace McpServer.Features.Tools.CallTool;

// FR-16, UC-08 基本フロー 2〜5, ADR-0024: MCP プロトコル面の tools/call。
//
// 🔴 **ツールをコードへ固定しない**（ADR-0024 が却下した案A に戻さない）。実行先は
// ToolCatalog / ToolInvocationService から動的に解決する。
//
// 本クラスは薄い変換層に留める。統制（登録確認・公開確認・個人資料の除外・越境判定・監査）は
// すべて ToolInvocationService の 1 経路にある。
public sealed class McpCallToolHandler(ToolInvocationService invocation)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<CallToolResult> CallToolAsync(
        ClaimsPrincipal user, string? toolName, string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return Error("ツール名がありません。");

        var outcome = await invocation.InvokeAsync(user, toolName, argumentsJson, ct);
        if (!outcome.Ok) return Error(outcome.Error!);

        return new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock
            {
                Text = JsonSerializer.Serialize(outcome.Result, JsonOptions)
            }]
        };
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
