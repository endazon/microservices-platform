using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.GetAttribute;

// FR-09, UC-05: 属性辞書の個別取得（管理者のみ）。
public static class GetAttributeEndpoint
{
    public static IEndpointRouteBuilder MapGetAttribute(this IEndpointRouteBuilder app)
    {
        app.MapGet("/attributes/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var attr = await db.AttributeDefinitions.FindAsync(id);
            return attr is null ? Results.NotFound() : Results.Ok(attr);
        });

        return app;
    }
}
