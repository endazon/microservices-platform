using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.ReplaceRoles;

// SC-17: ロール割当（差し替え。併任可）。
public static class ReplaceUserRolesEndpoint
{
    public static IEndpointRouteBuilder MapReplaceUserRoles(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{userId}/roles", async (
            string userId, ReplaceUserRolesRequest req,
            IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var assignable = await identity.ListAssignableRolesAsync(ct);
            var errors = UserAssignmentValidation.ValidateRoles(req.Roles, [.. assignable]);
            if (errors.Count > 0) return UserAdminEndpoints.ValidationProblem(errors);

            var updated = await identity.ReplaceRealmRolesAsync(userId, req.Roles, ct);
            return updated is null ? Results.NotFound() : Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }
}
