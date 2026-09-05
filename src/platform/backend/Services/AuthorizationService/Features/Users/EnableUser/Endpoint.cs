using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.EnableUser;

// SC-17: 再有効化。**セッションは復活しない**（本人が改めてログインする）。
public static class EnableUserEndpoint
{
    public static IEndpointRouteBuilder MapEnableUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{userId}/enable", async (
            string userId, IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, true, ct);
            return updated is null ? Results.NotFound() : Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }
}
