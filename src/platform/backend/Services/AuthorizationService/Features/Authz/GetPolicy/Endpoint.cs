using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.GetPolicy;

// FR-09, UC-05: ABAC ポリシー個別取得（管理者のみ）。
public static class GetPolicyEndpoint
{
    public static IEndpointRouteBuilder MapGetPolicy(this IEndpointRouteBuilder app)
    {
        app.MapGet("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        return app;
    }
}
