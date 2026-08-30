using McpServer.Domain;
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
                   McpDbContext db, TimeProvider clock) =>
        {
            var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == clientId);
            if (client is null) return Results.NotFound();

            if (client.Kind == McpClientKind.ServiceAccount)
            {
                var errors = ToolPublicationConfigValidator
                    .ValidateServiceAccountAttributes(clientId, req.Attributes);
                if (errors.Count > 0) return McpClientEndpoints.Problem(errors[0]);
            }

            client.ReplaceAttributes(req.Attributes, clock.GetUtcNow());
            await db.SaveChangesAsync();
            return Results.Ok(McpClientEndpoints.ToView(client));
        });

        return app;
    }
}
