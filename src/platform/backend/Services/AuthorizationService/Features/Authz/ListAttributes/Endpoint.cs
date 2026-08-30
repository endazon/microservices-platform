using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.ListAttributes;

// FR-09, UC-05: 属性辞書一覧（管理者のみ）。
public static class ListAttributesEndpoint
{
    public static IEndpointRouteBuilder MapListAttributes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/attributes", async (AuthorizationDbContext db) =>
            Results.Ok(await db.AttributeDefinitions.ToListAsync()));

        return app;
    }
}
