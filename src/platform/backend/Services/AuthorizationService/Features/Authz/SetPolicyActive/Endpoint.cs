using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.SetPolicyActive;

// FR-09, UC-05: ポリシー有効／無効の切替（削除せず一時停止。管理者のみ）。
public static class SetPolicyActiveEndpoint
{
    public static IEndpointRouteBuilder MapSetPolicyActive(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/policies/{id:guid}/active", async (
            Guid id, SetActiveRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            policy.SetActive(req.IsActive);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        return app;
    }
}
