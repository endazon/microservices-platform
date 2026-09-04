using AuthorizationService.Domain.Ports;
using AuthorizationService.Features.Users.DisableUser;
using AuthorizationService.Features.Users.EnableUser;
using AuthorizationService.Features.Users.ListAssignableRoles;
using AuthorizationService.Features.Users.ListUsers;
using AuthorizationService.Features.Users.ReplaceAttributes;
using AuthorizationService.Features.Users.ReplaceRoles;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Features.Users;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: 利用者アカウント管理スライスの合成点
// （ロール割当・ABAC 属性割当・無効化）。**システム管理者ロール限定**。
//
// ■ 責務の線（IADR-0301 決定 1）
//   SC-09 が属性体系・タグ辞書・ポリシーの「**定義**」を担い、本スライスは個々の利用者への
//   「**割当**」と無効化を担う（ADR-0026 §決定）。**本サービスは利用者の表を持たない** ——
//   一覧も割当も IdP（`IIdentityAdminClient`）へ委譲する。値域だけが `AttributeDefinitions`
//   （SC-09 の定義）から来る。
//
// ■ 🔴 **新規作成の口を作らない。**
//   計画 05_screens §SC-17 アクション:「アカウントは人事システム連携で自動プロビジョニングし…
//   （**本画面から新規作成はしない**）」。`POST /authz/users` を足すのは計画違反である。
//   不在は `UserAdminEndpointTests` が陽性対照つきで固定する。
//
// ADR-0065 決定 2: 1 ユースケースのファイルは操作フォルダへ束ねる。
// **本ファイルに残すのは、グループの構築と全操作が共有する `ValidationProblem` だけである**
// （写像は `PlatformUserMapper` が持つ）。
public static class UserAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // 05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」。運用者も不可。
        var g = app.MapGroup("/authz/users")
            .WithTags("UserAdmin")
            .RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        g.MapListUsers();
        // `assignable-roles` は 2 セグメント、`{userId}` 経路は 3 セグメントなので衝突しない。
        g.MapListAssignableRoles();
        g.MapReplaceUserAttributes();
        g.MapReplaceUserRoles();
        g.MapDisableUser();
        g.MapEnableUser();

        return app;
    }

    // 全操作が同じ形で返す（画面が 1 種類の読み方だけを覚えれば済む）。集約直下に置く点は変えず、
    // 実体は手書きをやめて Riok.Mapperly の生成マッパ（`PlatformUserMapper.ToDto`）へ移した
    // （計画 ADR-0030 §決定 / IADR-0371 決定 3 / IADR-0376）。**このクラスに写像は残さない。**

    // RFC7807 準拠のバリデーションエラー（400）。AuthzEndpoints と同じ形へ揃える
    // （画面が 2 種類の読み方を覚えなくて済む）。
    internal static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] = errors.ToArray()
        });
}
