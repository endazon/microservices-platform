using McpServer.Domain;
using McpServer.Domain.Ports;
using McpServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpServer.Features.McpClients.ReplaceAttributes;

// FR-16, UC-09 基本フロー 1: 無人アカウントへの ABAC 属性割当（差し替え）。
public static class ReplaceMcpClientAttributesEndpoint
{
    public static IEndpointRouteBuilder MapReplaceMcpClientAttributes(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{clientId}/attributes",
            async (string clientId, ReplaceMcpClientAttributesRequest req,
                   McpDbContext db, TimeProvider clock,
                   IRegistrarAttributeResolver registrar, CancellationToken ct) =>
        {
            var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
            if (client is null) return Results.NotFound();

            // 🔴 ADR-0034 決定 9 / ADR-0062 決定 2・3: 登録経路と**同じ 1 つの関数**が判定する。
            // 登録だけ塞いで差し替えが緩い形にしない（片方だけ直したときに黙ってズレる）。
            var rejected = await McpClientEndpoints.RejectUnassignableAsync(
                clientId, client.Kind, req.Attributes, registrar, ct);
            if (rejected is not null) return rejected;

            client.ReplaceAttributes(req.Attributes, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.Ok(McpClientEndpoints.ToView(client));
        });

        return app;
    }
}
