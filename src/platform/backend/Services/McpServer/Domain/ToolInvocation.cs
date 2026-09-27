
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

// FR-16: 下流サービスのツール実体を呼び出す口。本番の実装（GrpcToolInvoker）と
// テスト用の差し替えを分けるために抽象を 1 つだけ置く。
//
// ［2026-09-27 追記 / #1516, ADR-0117 決定 1］🔴 **宛先は `PublishedTool.Service`（申告したサービス）と申告名だけで決める。**
// 申告の中身から宛先を作らない（申告に URL は無い）。実行できないとき（経路が無い・実行口が無い・時間切れ・拒否）は
// `ToolExecutionUnavailableException` を投げる。呼び出し側の取り消しは OperationCanceledException のまま外へ出す。
public interface IToolInvoker
{
    Task<McpToolResult> InvokeAsync(
        PublishedTool tool, ToolInvocationScope scope, string argumentsJson, CancellationToken ct);
}

// FR-16, UC-08 例外フロー, ADR-0117 決定 4（#1516）: ツールを実行できなかったこと（fail-closed）。
//
// `Message` は **MCP クライアントへそのまま返す文言**であり、内部のサービス名・アドレス・status を含めない
// （それらはログにだけ書く）。ToolInvocationService がこれを拒否（`ToolInvocationOutcome.Rejected`）へ写す ——
// 結果は 1 件も返さない。
public sealed class ToolExecutionUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// FR-16, UC-08: ツール呼び出しの結果。
// Error が非 null なら呼び出しは拒否・失敗であり、Result は null である。
public sealed record ToolInvocationOutcome(McpToolResult? Result, string? Error)
{
    public bool Ok => Error is null;

    public static ToolInvocationOutcome Success(McpToolResult result) => new(result, null);

    public static ToolInvocationOutcome Rejected(string error) => new(null, error);
}
