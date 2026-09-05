using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Features.McpClients.ListClients;

// FR-16, UC-09 基本フロー 3, SC-12: 登録クライアントの提示（GET /mcp-clients）。
public static class ListMcpClientsEndpoint
{
    public static IEndpointRouteBuilder MapListMcpClients(this IEndpointRouteBuilder app)
    {
        app.MapGet("", async (McpDbContext db) =>
            Results.Ok((await db.Clients.AsNoTracking().OrderBy(c => c.ClientId).ToListAsync())
                .Select(McpClientMapper.ToView).ToList()));

        return app;
    }
}
