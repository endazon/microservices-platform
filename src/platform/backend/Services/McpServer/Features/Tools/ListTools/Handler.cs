using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace McpServer.Features.Tools.ListTools;

// FR-16, UC-08 基本フロー 1, ADR-0024: MCP プロトコル面の tools/list。
//
// 🔴 **ツールをコードへ固定しない。** 公式 SDK の属性ベース登録（[McpServerTool]）を使うと
// 公開ツールがコンパイル時に決まり、「サービス追加のたびに MCP サーバーを改修する」案A に
// 戻ってしまう（ADR-0024 が却下した選択肢である）。したがって**動的ハンドラ**を用い、
// 一覧は ToolCatalog / ToolInvocationService から解決する。
//
// 本クラスは薄い変換層に留める。統制（登録確認・公開確認・個人資料の除外・越境判定・監査）は
// すべて ToolInvocationService の 1 経路にある。
public sealed class McpListToolsHandler(ToolInvocationService invocation)
{
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement;

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

    // 申告の input_schema は JSON 文字列で受け取る。壊れていれば空のオブジェクトスキーマへ
    // 縮退させる（一覧そのものが落ちて全ツールが見えなくなる方が害が大きい）。
    private static JsonElement ParseSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) return EmptyObjectSchema;
        try { return JsonDocument.Parse(schema).RootElement.Clone(); }
        catch (JsonException) { return EmptyObjectSchema; }
    }
}
