using McpServer.Infrastructure.Persistence;

namespace McpServer.Features.McpClients.EnableClient;

// FR-16, UC-09 / SC-12: クライアントの再有効化。無効化と同じく**次の呼び出しから即座に効く**。
public static class EnableMcpClientEndpoint
{
    public static IEndpointRouteBuilder MapEnableMcpClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{clientId}/enable", (string clientId, McpDbContext db, TimeProvider clock) =>
            McpClientEndpoints.SetEnabledAsync(clientId, true, db, clock));

        return app;
    }
}
