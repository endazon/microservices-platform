using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Features.Authz.ListPolicies;

// FR-09, UC-05: ABAC ポリシー一覧（管理者のみ。認可は合成点の admin グループが担う）。
public static class ListPoliciesEndpoint
{
    public static IEndpointRouteBuilder MapListPolicies(this IEndpointRouteBuilder app)
    {
        app.MapGet("/policies", async (AuthorizationDbContext db) =>
            Results.Ok(await db.Policies.ToListAsync()));

        return app;
    }
}
