using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients.DisableClient;

// FR-16, UC-09 / SC-12: クライアントの無効化。**次の呼び出しから即座に効く**
// （McpSubjectResolver は毎回登録簿を引き、キャッシュを挟まない）。
// ［2026-10-09 / #1829］無人の行は登録簿の後で IdP のクライアントの `enabled` へも写す（[[IADR-0516]] 決定 4a。順序と失敗の扱いは
// `McpClientEndpoints.SetEnabledAsync`）。
public static class DisableMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapDisableMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{clientId}/disable", (string clientId, McpDbContext db, TimeProvider clock,
                IServiceAccountProvisioner provisioner, ILoggerFactory loggers, IAuditLogger audit, HttpContext http,
                CancellationToken ct) =>
            // FR-16, SC-12, 計画 ADR-0134 フォローアップ 5（#1845）: 結果を監査に残す（McpClientAudit）。
            McpClientAudit.RecordAsync(audit, http.User, McpClientAudit.DisableAction, McpClientAudit.Detail(clientId),
                () => McpClientEndpoints.SetEnabledAsync(clientId, false, db, clock, provisioner,
                    loggers.CreateLogger(typeof(DisableMcpClientEndpoint)), ct)));

        return app;
    }
}
