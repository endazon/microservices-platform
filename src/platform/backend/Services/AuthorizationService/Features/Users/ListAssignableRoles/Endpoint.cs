using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.ListAssignableRoles;

// SC-17 入力規則「定義済みロールのみ」の**値域の正**。
// 画面はこれを引いて選択肢を作る（焼き込まない）。
public static class ListAssignableRolesEndpoint
{
    public static IEndpointRouteBuilder MapListAssignableRoles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/assignable-roles", async (IIdentityAdminClient identity, CancellationToken ct) =>
            Results.Ok(await identity.ListAssignableRolesAsync(ct)));

        return app;
    }
}
