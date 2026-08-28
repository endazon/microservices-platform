
namespace McpServer.Domain;

// FR-16, UC-08, ADR-0004: MCP サーバーから下流サービスへ渡す実行スコープ。
//
// 🔴 **ExcludePrivateNote は要求側の 1 層目である**（ADR-0034 決定 9）。応答側のフィルタ
// （ServiceAccountDocumentFilter）と二重に持ち、片方だけにしない。
//
// MCP サーバー自身は認可判定を持たず、各サービス（および認可サービス）へ委譲する
// （11_mcp-server-integration §3）。本レコードはその委譲のための入力である。
public sealed record ToolInvocationScope(
    string SubjectId,
    string SubjectKind,
    IReadOnlyDictionary<string, string> SubjectAttributes,
    bool ExcludePrivateNote,
    string RequiredScope);

// FR-16: 下流サービスのツール実体を呼び出す口。HTTP 実装（HttpToolInvoker）と
// テスト用の差し替えを分けるために抽象を 1 つだけ置く。
public interface IToolInvoker
{
    Task<McpToolResult> InvokeAsync(
        PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct);
}

// FR-16, UC-08: ツール呼び出しの結果。
// Error が非 null なら呼び出しは拒否・失敗であり、Result は null である。
public sealed record ToolInvocationOutcome(McpToolResult? Result, string? Error)
{
    public bool Ok => Error is null;

    public static ToolInvocationOutcome Success(McpToolResult result) => new(result, null);

    public static ToolInvocationOutcome Rejected(string error) => new(null, error);
}
