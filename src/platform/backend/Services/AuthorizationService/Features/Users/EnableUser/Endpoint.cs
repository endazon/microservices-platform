using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.EnableUser;

// SC-17: 再有効化。**セッションは復活しない**（本人が改めてログインする）。
//
// ★ FR-19, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] (#1392):
//   **保持起点（無効化日）を消す。** 再有効化は「退職の取り消し・誤操作の是正」であり、
//   起点が残ると**復職した利用者の個人資料が退職者と同じ期限で扱われ続ける。**
//   消すのは無効化由来の起点だけである —— 人事由来の退職日（`hr_leave_date`）は
//   人事システムが所有し、こちらから書き換えない。
public static class EnableUserEndpoint
{
    public static IEndpointRouteBuilder MapEnableUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{userId}/enable", async (
            string userId, IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, true, ct);
            if (updated is null) return Results.NotFound();

            if (updated.Attributes.ContainsKey(RetentionAnchorAttributes.AccountDisabledAtKey))
            {
                updated = await identity.SetRetentionAnchorAsync(
                    userId, RetentionAnchorAttributes.AccountDisabledAtKey, null, ct) ?? updated;
            }

            return Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }
}
