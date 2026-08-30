using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.UpdatePolicy;

// FR-09, UC-05: ABAC ポリシー更新（保存前に矛盾検証。管理者のみ）。
//
// 🔴 検証は `AuthzEndpoints.ValidatePolicyAsync` を**登録・dry-run と共有する**（#535）。
public static class UpdatePolicyEndpoint
{
    public static IEndpointRouteBuilder MapUpdatePolicy(this IEndpointRouteBuilder app)
    {
        app.MapPut("/policies/{id:guid}", async (
            Guid id, CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var policy = await db.Policies.FindAsync(id);
            if (policy is null)
                return Results.NotFound();

            var errors = await AuthzEndpoints.ValidatePolicyAsync(req, db);
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            policy.Update(req.Name, req.Action, req.UserConditions, req.DocumentConditions);
            await db.SaveChangesAsync();
            return Results.Ok(policy);
        });

        return app;
    }
}
