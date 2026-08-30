using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.ListUsers;

// SC-17 主要素 1: 利用者一覧（部門・ロール・ABAC 属性・状態）。
// **部門は属性 `department` そのものである**（DTO へ複写しない）。
public static class ListUsersEndpoint
{
    public static IEndpointRouteBuilder MapListUsers(this IEndpointRouteBuilder app)
    {
        app.MapGet("", async (IIdentityAdminClient identity, CancellationToken ct) =>
            Results.Ok((await identity.ListUsersAsync(ct)).Select(UserAdminEndpoints.ToDto).ToList()));

        return app;
    }
}
