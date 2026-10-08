using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;

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
                IServiceAccountProvisioner provisioner, ILoggerFactory loggers, CancellationToken ct) =>
            McpClientEndpoints.SetEnabledAsync(clientId, false, db, clock, provisioner,
                loggers.CreateLogger(typeof(DisableMcpClientEndpoint)), ct));

        return app;
    }
}
