using AuthorizationService.Domain;
using AuthorizationService.Domain.Ports;
using AuthorizationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Features.Users;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: 利用者アカウント管理
// （ロール割当・ABAC 属性割当・無効化）。**システム管理者ロール限定**。
//
// ■ 責務の線（IADR-0301 決定 1）
//   SC-09 が属性体系・タグ辞書・ポリシーの「**定義**」を担い、本エンドポイントは個々の利用者への
//   「**割当**」と無効化を担う（ADR-0026 §決定）。**本サービスは利用者の表を持たない** ——
//   一覧も割当も IdP（`IIdentityAdminClient`）へ委譲する。値域だけが `AttributeDefinitions`
//   （SC-09 の定義）から来る。
//
// ■ 🔴 **新規作成の口を作らない。**
//   計画 05_screens §SC-17 アクション:「アカウントは人事システム連携で自動プロビジョニングし…
//   （**本画面から新規作成はしない**）」。`POST /authz/users` を足すのは計画違反である。
//   不在は `UserAdminEndpointTests` が陽性対照つきで固定する。
public static class UserAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // 05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」。運用者も不可。
        var g = app.MapGroup("/authz/users")
            .WithTags("UserAdmin")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // SC-17 主要素 1: 利用者一覧（部門・ロール・ABAC 属性・状態）。
        // **部門は属性 `department` そのものである**（DTO へ複写しない）。
        g.MapGet("", async (IIdentityAdminClient identity, CancellationToken ct) =>
            Results.Ok((await identity.ListUsersAsync(ct)).Select(ToDto).ToList()));

        // SC-17 入力規則「定義済みロールのみ」の**値域の正**。
        // 画面はこれを引いて選択肢を作る（焼き込まない）。
        // `assignable-roles` は 2 セグメント、下の `{userId}` 経路は 3 セグメントなので衝突しない。
        g.MapGet("/assignable-roles", async (IIdentityAdminClient identity, CancellationToken ct) =>
            Results.Ok(await identity.ListAssignableRolesAsync(ct)));

        // SC-17: ABAC 属性の割当（差し替え）。
        // 値域は SC-09 の属性辞書（`scope=user`）が持つ。**必須は部門・機密区分上限、タグは任意。**
        g.MapPut("/{userId}/attributes", async (
            string userId, ReplaceUserAttributesRequest req,
            IIdentityAdminClient identity, AuthorizationDbContext db, CancellationToken ct) =>
        {
            var definitions = await db.AttributeDefinitions.AsNoTracking().ToListAsync(ct);
            var errors = UserAssignmentValidation.ValidateAttributes(req.Attributes, definitions);
            if (errors.Count > 0) return ValidationProblem(errors);

            var updated = await identity.ReplaceAttributesAsync(userId, req.Attributes, ct);
            return updated is null ? Results.NotFound() : Results.Ok(ToDto(updated));
        });

        // SC-17: ロール割当（差し替え。併任可）。
        g.MapPut("/{userId}/roles", async (
            string userId, ReplaceUserRolesRequest req,
            IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var assignable = await identity.ListAssignableRolesAsync(ct);
            var errors = UserAssignmentValidation.ValidateRoles(req.Roles, [.. assignable]);
            if (errors.Count > 0) return ValidationProblem(errors);

            var updated = await identity.ReplaceRealmRolesAsync(userId, req.Roles, ct);
            return updated is null ? Results.NotFound() : Results.Ok(ToDto(updated));
        });

        // SC-17 アクション:「無効化→**全セッション即時失効**」。
        //
        // 🔴 **2 段は分けられない。** 無効化だけでは既存のセッションが生き残る（アクセストークンの
        // 寿命だけ効き続ける）。**無効化してから失効させる** —— 逆順だと、失効と無効化の間に
        // 張り直されたセッションが残る。
        // 失効は IdP のバックチャネルログアウトを起こし、BFF の BackchannelLogoutProcessor が
        // subject 単位でチケットを消す（ADR-0032 / IADR-0273）。
        g.MapPost("/{userId}/disable", async (
            string userId, IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, false, ct);
            if (updated is null) return Results.NotFound();
            await identity.RevokeSessionsAsync(userId, ct);
            return Results.Ok(ToDto(updated));
        });

        // SC-17: 再有効化。**セッションは復活しない**（本人が改めてログインする）。
        g.MapPost("/{userId}/enable", async (
            string userId, IIdentityAdminClient identity, CancellationToken ct) =>
        {
            var updated = await identity.SetEnabledAsync(userId, true, ct);
            return updated is null ? Results.NotFound() : Results.Ok(ToDto(updated));
        });

        return app;
    }

    private static PlatformUserDto ToDto(IdentityUser user)
        => new(user.Id, user.Username, user.DisplayName, user.Enabled,
            [.. user.Roles], new Dictionary<string, string>(user.Attributes));

    // RFC7807 準拠のバリデーションエラー（400）。AuthzEndpoints と同じ形へ揃える
    // （画面が 2 種類の読み方を覚えなくて済む）。
    private static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] = errors.ToArray()
        });
}
