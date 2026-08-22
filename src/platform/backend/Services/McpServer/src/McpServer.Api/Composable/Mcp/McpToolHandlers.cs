using System.Security.Claims;
using System.Text.Json;
using McpServer.Api.Foundation.Services;
using ModelContextProtocol.Protocol;

namespace McpServer.Api.Composable.Mcp;

// FR-16, UC-08, ADR-0024: MCP プロトコル面（tools/list・tools/call）。
//
// 🔴 **ツールをコードへ固定しない。** 公式 SDK の属性ベース登録（[McpServerTool]）を使うと
// 公開ツールがコンパイル時に決まり、「サービス追加のたびに MCP サーバーを改修する」案A に
// 戻ってしまう（ADR-0024 が却下した選択肢である）。したがって**動的ハンドラ**を用い、
// 一覧・実行とも ToolCatalog / ToolInvocationService から解決する。
//
// 本クラスは薄い変換層に留める。統制（登録確認・公開確認・個人資料の除外・越境判定・監査）は
// すべて ToolInvocationService の 1 経路にある。
public sealed class McpToolHandlers(ToolInvocationService invocation)
{
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement;

    // UC-08 基本フロー 1: 公開ツール一覧。
    public async ValueTask<ListToolsResult> ListToolsAsync(
        ClaimsPrincipal user, CancellationToken ct)
    {
        var tools = await invocation.ListToolsAsync(user, ct);
        return new ListToolsResult
        {
            Tools = [.. tools.Select(t => new Tool
            {
                Name = t.PublishedName,
                Description = t.Declaration.Description,
                InputSchema = ParseSchema(t.Declaration.InputSchema)
            })]
        };
    }

    // UC-08 基本フロー 2〜5: ツール実行。
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    // 申告の input_schema は JSON 文字列で受け取る。壊れていれば空のオブジェクトスキーマへ
    // 縮退させる（一覧そのものが落ちて全ツールが見えなくなる方が害が大きい）。
    private static JsonElement ParseSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return EmptyObjectSchema;
        try { return JsonDocument.Parse(schema).RootElement.Clone(); }
        catch (JsonException) { return EmptyObjectSchema; }
    }
}
