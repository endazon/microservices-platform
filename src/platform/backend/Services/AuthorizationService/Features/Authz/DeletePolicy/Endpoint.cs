using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.DeletePolicy;

// FR-09, UC-05: ABAC ポリシー削除（管理者のみ）。
public static class DeletePolicyEndpoint
{
    public static IEndpointRouteBuilder MapDeletePolicy(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/policies/{id:guid}", async (Guid id, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            db.Policies.Remove(policy);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}
