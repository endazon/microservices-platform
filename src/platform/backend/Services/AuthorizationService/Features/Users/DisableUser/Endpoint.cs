using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Users.DisableUser;

// SC-17 アクション:「無効化→**全セッション即時失効**」。
//
// 🔴 **2 段は分けられない。** 無効化だけでは既存のセッションが生き残る（アクセストークンの
// 寿命だけ効き続ける）。**無効化してから失効させる** —— 逆順だと、失効と無効化の間に
// 張り直されたセッションが残る。
// 失効は IdP のバックチャネルログアウトを起こし、BFF の BackchannelLogoutProcessor が
// subject 単位でチケットを消す（ADR-0032 / IADR-0273）。
//
// ★ FR-19, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] (#1392):
//   **3 段目として保持起点を刻む。** D-09 の「退職日から 30 日間」の退職日を取得する経路は
//   存在せず（人事連携は未実装・Keycloak の `enabled` に日付は無い）、ADR-0082 決定 5 が
//   暫定の起点を「**SC-17 でアカウントが無効化された日**」と定めた。**その日を持つのはここだけ**である
//   —— 刻まなければ、後から「いつ無効化されたのか」を答えられる場所がどこにも無い。
public static class DisableUserEndpoint
{
    public static IEndpointRouteBuilder MapDisableUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{userId}/disable", async (
            string userId, IIdentityAdminClient identity, RetentionAnchorOptions anchorOptions,
            TimeProvider clock, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, false, ct);
            if (updated is null) return Results.NotFound();
            await identity.RevokeSessionsAsync(userId, ct);

            updated = await StampRetentionAnchorAsync(
                identity, userId, updated, anchorOptions, clock, ct);
            return Results.Ok(PlatformUserMapper.ToDto(updated));
        });

        return app;
    }

    // FR-19, ADR-0082 決定 5, [[IADR-0428]]: 保持起点の刻印。
    //
    // 🔴 **既にある起点は上書きしない**（冪等）。上書きすると、再無効化のたびに 30 日の窓が
    // 後ろへ延びる —— 無効化は失効を確実にするために繰り返し押される操作である。
    //
    // 🔴 **`Source=hr-leave-date` のときは刻まない。** そちらの起点の出所は人事システムであり、
    // 管理者の無効化操作ではない。**人事連携は未実装なので起点は未供給のままになり、
    // 判定は「数えていない」（削除しない）へ倒れる** —— それが正しい振る舞いである。
    private static async Task<IdentityUser> StampRetentionAnchorAsync(
        IIdentityAdminClient identity, string userId, IdentityUser updated,
        RetentionAnchorOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (options.Source != RetentionAnchorSource.AccountDisabledAt) return updated;

        // 読めない値（`"0"` 等）が載っていたら刻み直す —— 窓を計算できない値を残すより、
        // 「無効化した今」を起点にするほうが（窓が後ろへずれる＝利用者に有利な側であり）安全である。
        var anchor = RetentionAnchorPolicy.Resolve(updated.Attributes, options);
        if (anchor.State == RetentionAnchorState.Supplied) return updated;

        return await identity.SetRetentionAnchorAsync(
            userId, options.AttributeKey, clock.GetUtcNow(), ct) ?? updated;
    }
}
