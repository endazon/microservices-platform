using Platform.Shared.Infrastructure.Foundation.Observability;

namespace McpServer.Domain;

// FR-16, UC-08, ADR-0117 決定 3, ADR-0086 決定 1, ADR-0088 決定 1, [[IADR-0479]] (#1611): MCP サーバーから下流サービスへ**本文で運ぶ利用者文脈**。
//
// 🔴 **運ぶのは利用者と操作だけである。** 従前の実行スコープ（主体・種別・属性・個人資料の除外制約・必要スコープ）は運ばない ——
//   受けたサービスはこの利用者文脈で認可サービスへ判定を問い、**自分で**認可する。属性は認可サービスが引き直す（ADR-0088）。
//   解決済みの scope を運んで信じさせる形は採らない（ADR-0117 決定 3。[[IADR-0292]] D-1）。
//
// 🔴 **サービスアカウント実行の `UserId` は `service-account-<client>`**（Keycloak が client credentials の主体へ付ける利用者名の形）。
//   受けたサービスはこの接頭辞から ADR-0034 決定 9（個人資料の一律除外）を導く（要求側の 1 層目）。応答側のフィルタ
//   （`ServiceAccountDocumentFilter`）は本サービスが別に持つ（2 層目）。
//   client の識別子をそのまま `UserId` にしない —— 同じ綴りの利用者が居れば、その人として判定される。
//
// MCP サーバー自身は認可判定を持たない（11_mcp-server-integration §3）。
public sealed record ToolUserContext(string UserId, string Action)
{
    /// <summary>公開するツールはすべて読み取りである（11_mcp-server-integration §6「副作用の無い読み取りのみ」）。</summary>
    public const string ReadAction = "read";

    /// <summary>
    /// 解決済みの主体から利用者文脈を組む。**有人で利用者名が無ければ null**（主体の分からない実行を下流へ送らない）。
    /// </summary>
    public static ToolUserContext? For(McpSubject subject)
    {
        if (subject.IsServiceAccount)
            return new(ServiceAccountUserName(subject.ClientId), ReadAction);

        return string.IsNullOrWhiteSpace(subject.UserName) ? null : new(subject.UserName, ReadAction);
    }

    /// <summary>
    /// FR-16, SC-12, 計画 ADR-0123 決定 1・フォローアップ 3, [[IADR-0515]] (#1786): クライアントのサービスアカウントの利用者名。
    /// 🔴 **組み立ては ここ 1 か所である。** 実行時に下流へ運ぶ `user_id` と、SC-12 の登録で属性を書く先
    /// （<c>IServiceAccountProvisioner</c> が認可サービスと同じ照会で引き直す利用者）が同じ綴りでなければ、
    /// 書いた属性は判定に使われない。Keycloak はサービスアカウントの利用者名を小文字で持つ。
    /// </summary>
    public static string ServiceAccountUserName(string clientId)
        => MachinePrincipal.ServiceAccountUsernamePrefix + clientId.ToLowerInvariant();
}

// FR-16: 下流サービスのツール実体を呼び出す口。本番の実装（GrpcToolInvoker）と
// テスト用の差し替えを分けるために抽象を 1 つだけ置く。
//
// ［2026-09-27 追記 / #1516, ADR-0117 決定 1］🔴 **宛先は `PublishedTool.Service`（申告したサービス）と申告名だけで決める。**
// 申告の中身から宛先を作らない（申告に URL は無い）。実行できないとき（経路が無い・実行口が無い・時間切れ・拒否）は
// `ToolExecutionUnavailableException` を投げる。呼び出し側の取り消しは OperationCanceledException のまま外へ出す。
public interface IToolInvoker
{
    Task<McpToolResult> InvokeAsync(
        PublishedTool tool, ToolUserContext user, string argumentsJson, CancellationToken ct);
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
