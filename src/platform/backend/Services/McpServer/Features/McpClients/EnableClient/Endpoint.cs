using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Audit;

namespace McpServer.Features.McpClients.EnableClient;

// FR-16, UC-09 / SC-12: クライアントの再有効化。無効化と同じく**次の呼び出しから即座に効く**。
// ［2026-10-09 / #1829］無人の行は IdP のクライアントの `enabled` を先に書いてから登録簿へ書く（[[IADR-0516]] 決定 4a。接続を開く側なので
// 決定 4 と同じ「IdP が先」。順序と失敗の扱いは `McpClientEndpoints.SetEnabledAsync`）。
public static class EnableMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapEnableMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{clientId}/enable", (string clientId, McpDbContext db, TimeProvider clock,
                IServiceAccountProvisioner provisioner, ILoggerFactory loggers, IAuditLogger audit, HttpContext http,
                CancellationToken ct) =>
            // FR-16, SC-12, 計画 ADR-0134 フォローアップ 5（#1845）: 結果を監査に残す（McpClientAudit）。
            McpClientAudit.RecordAsync(audit, http.User, McpClientAudit.EnableAction, McpClientAudit.Detail(clientId),
                () => McpClientEndpoints.SetEnabledAsync(clientId, true, db, clock, provisioner,
                    loggers.CreateLogger(typeof(EnableMcpClientEndpoint)), ct)));

        return app;
    }
}
