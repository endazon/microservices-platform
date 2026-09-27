using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace GraphService.Features.McpTools.Execute;

// FR-16, NFR-09, 計画 ADR-0117 決定 3, ADR-0086 決定 1・§結果, [[IADR-0479]]（2026-09-27 追記 / #1611 段 3）:
// **gRPC `platform.mcp.v1.McpToolExecution/Execute` の本文の利用者文脈（`McpToolUserContext`）を信じてよい呼び出し元の集合。**
// RetrievalService の同名の型（段 1）と同じ形である。
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文の `user_id` を信じると、どれもが任意の利用者を名乗り、
// その利用者のスコープで文書の関係（題名・属性）を引ける。ツールの実行口を利用者の権限で呼ぶ中継者は
// MCP サーバー（`GrpcToolInvoker`）だけである（ADR-0117 決定 3）。依存をそこへ狭める。
//
// 🔴 **既定は `mcp-server` だけ**（helm の mcp の `serviceToken.clientId`・compose の `ServiceToken__ClientId`・realm の
//   機密クライアント。`McpToolExecutionRelayDeploymentWiringTests` が固定する）。**構成したら既定を置き換える**。
//   空白だけなら誰も信じない（fail-closed）。
// 🔴 同じ GraphService の `GraphNeighbors:`（既定 `retrieval-service`）とはキーも集合も共有しない —— 中継者は面ごとに違う。
// 判定・既定の解決・構成の形の検査は共有の `TrustedUserContextRelay` が持つ（面ごとに写さない）。
public sealed class McpToolExecutionRelayOptions
{
    public const string SectionName = "McpToolExecution";

    /// <summary>未構成のときの許可集合。MCP サーバーの s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["mcp-server"];

    /// <summary>本文の利用者文脈を信じる呼び出し元のクライアント識別子（`azp`）。null（未構成）なら既定。構成すると既定を置き換える。</summary>
    public string[]? TrustedUserContextClients { get; set; }

    /// <summary>実際に使う許可集合（既定の解決・前後空白の除去・空要素の除去の後）。</summary>
    public IReadOnlyList<string> EffectiveClients =>
        TrustedUserContextRelay.Effective(TrustedUserContextClients, DefaultTrustedUserContextClients);

    /// <summary>呼び出し元が本文の利用者文脈を運んでよいか（機械の主体 ∧ クライアント識別が序数一致で許可集合に在る）。</summary>
    public bool TrustsUserContextFrom(ClaimsPrincipal? caller) =>
        TrustedUserContextRelay.Trusts(caller, EffectiveClients);

    /// <summary>1 つの値（配列でない）で構成されていたら起動時に止める（既定へ静かに戻るのを防ぐ）。</summary>
    public static void ThrowIfScalar(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        TrustedUserContextRelay.ThrowIfScalar(configuration, SectionName, DefaultTrustedUserContextClients[0]);
}

// FR-16, #1611 段 3: 実行口の登録（`Program.cs` から 1 行で呼ぶ）。**`ThrowIfScalar` → `Configure` の順**
// （許可集合の形を起動時に確かめてから束ねる。逆順だと 1 つの値が既定へ静かに戻った状態で束ねられる）。
public static class McpToolExecutionRegistration
{
    public static IServiceCollection AddMcpToolExecution(this IServiceCollection services, IConfiguration configuration)
    {
        McpToolExecutionRelayOptions.ThrowIfScalar(configuration);
        services.Configure<McpToolExecutionRelayOptions>(
            configuration.GetSection(McpToolExecutionRelayOptions.SectionName));
        return services;
    }
}
