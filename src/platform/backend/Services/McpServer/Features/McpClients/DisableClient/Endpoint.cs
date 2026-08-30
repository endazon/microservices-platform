using McpServer.Infrastructure.Persistence;

namespace McpServer.Features.McpClients.DisableClient;

// FR-16, UC-09 / SC-12: クライアントの無効化。**次の呼び出しから即座に効く**
// （McpSubjectResolver は毎回登録簿を引き、キャッシュを挟まない）。
public static class DisableMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapDisableMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{clientId}/disable", (string clientId, McpDbContext db, TimeProvider clock) =>
            McpClientEndpoints.SetEnabledAsync(clientId, false, db, clock));

        return app;
    }
}
