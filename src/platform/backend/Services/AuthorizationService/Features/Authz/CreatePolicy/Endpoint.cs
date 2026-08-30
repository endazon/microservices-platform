using AuthorizationService.Domain;
using AuthorizationService.Infrastructure.Persistence;

namespace AuthorizationService.Features.Authz.CreatePolicy;

// FR-09, UC-05: ABAC ポリシー登録（保存前に矛盾検証。管理者のみ）。
//
// 🔴 検証は `AuthzEndpoints.ValidatePolicyAsync` を**更新・dry-run と共有する**（#535）。
// ここへ複製すると「検証は通ったのに保存で矛盾が出る」が構造的に可能になる。
public static class CreatePolicyEndpoint
{
    public static IEndpointRouteBuilder MapCreatePolicy(this IEndpointRouteBuilder app)
    {
        app.MapPost("/policies", async (CreatePolicyRequest req, AuthorizationDbContext db) =>
        {
            var errors = await AuthzEndpoints.ValidatePolicyAsync(req, db);
            if (errors.Count > 0)
                return AuthzEndpoints.ValidationProblem(errors);

            var policy = AbacPolicy.Create(req.Name, req.Action,
                req.UserConditions, req.DocumentConditions);
            db.Policies.Add(policy);
            await db.SaveChangesAsync();
            return Results.Created($"/authz/policies/{policy.Id}", policy);
        });

        return app;
    }
}
