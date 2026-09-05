using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.DisableUser;

// SC-17 アクション:「無効化→**全セッション即時失効**」。
//
// 🔴 **2 段は分けられない。** 無効化だけでは既存のセッションが生き残る（アクセストークンの
// 寿命だけ効き続ける）。**無効化してから失効させる** —— 逆順だと、失効と無効化の間に
// 張り直されたセッションが残る。
// 失効は IdP のバックチャネルログアウトを起こし、BFF の BackchannelLogoutProcessor が
// subject 単位でチケットを消す（ADR-0032 / IADR-0273）。
public static class DisableUserEndpoint
{
    public static IEndpointRouteBuilder MapDisableUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{userId}/disable", async (
            string userId, IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, false, ct);
            if (updated is null) return Results.NotFound();
            await identity.RevokeSessionsAsync(userId, ct);
            return Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }
}
