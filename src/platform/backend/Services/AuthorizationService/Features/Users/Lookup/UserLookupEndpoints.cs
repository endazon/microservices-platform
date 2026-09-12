using AuthorizationService.Features.Users.Lookup.LookupUsers;
using AuthorizationService.Features.Users.Lookup.ResolveUsers;

namespace AuthorizationService.Features.Users.Lookup;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
// **共有先に指定する利用者を探す／表示名へ引く**読み口の合成点。
//
// ■ 🔴 **AdminOnly の管理面（`UserAdminEndpoints`）とは別の群である。**
//   同じ接頭辞（`/authz/users`）を共有するが、**`MapGroup` を分けてある** ——
//   あちらの群に足すと `PlatformAuthPolicies.AdminOnly` が掛かり、一般利用者が 403 になる
//   （共有は所有者＝一般利用者の操作であり、管理者限定にすると誰も共有先を選べない）。
//   逆に、こちらの群へ管理操作を足してはならない（**認可が緩む向き**）。
//
// ■ 認可は **認証のみ・ロール不問**（ADR-0098 決定 1）
//   計画は「画面には表示名を出し、識別子は出さない」と定める。表示名を出すには利用者名簿を
//   一般利用者が引けなければならない。**面に出す項目を 3 つ（利用者名・表示名・有効状態）へ
//   閉じる**ことで釣り合いを取る —— ロール・ABAC 属性・内部 ID を運ぶ項目が
//   `UserSummaryDto` に無い（型で閉じる。`UserDirectoryGrpcService` が s2s で採ったのと同じ作法）。
//
// ■ 🔴 **書き込みの口を作らない。** この群は読み取りだけである（新規作成の不在は
//   `IdentityAdminContractTests` がポートの側で固定する）。
//
// ■ 2 つの口の違い（**片方で代用できない**）
//   - `lookup`: 名前の一部から**候補を探す**。有効な利用者だけ（退職者を新たな共有先にしない）。
//   - `resolve`: 台帳にある利用者名を**表示名へ引く**。**無効化済みも返す** ——
//     既存の共有先が「（不明な利用者）」に化けてしまうと、取り消すべき相手が画面から消える。
public static class UserLookupEndpoints
{
    public static IEndpointRouteBuilder MapUserLookupEndpoints(this IEndpointRouteBuilder app)
    {
        // 🔴 **`RequireAuthorization()` にロールを渡さない**（上の「認可」を参照）。
        var g = app.MapGroup("/authz/users")
            .WithTags("UserLookup")
            .RequireAuthorization();

        // `lookup` / `resolve` は literal 1 段（2 セグメント）であり、AdminOnly 群の
        // `{userId}` 経路（3 セグメント）とも `assignable-roles` とも衝突しない。
        g.MapLookupUsers();
        g.MapResolveUsers();

        return app;
    }
}
