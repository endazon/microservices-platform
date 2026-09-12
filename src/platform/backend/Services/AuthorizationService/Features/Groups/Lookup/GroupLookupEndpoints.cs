using AuthorizationService.Features.Groups.Lookup.LookupGroups;
using AuthorizationService.Features.Groups.Lookup.ResolveGroups;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Features.Groups.Lookup;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, ADR-0100 フォローアップ 2,
// [[IADR-0447]] / [[IADR-0449]] (#1447):
// **共有先に指定するグループを探す／表示名へ引く**読み口の合成点。
//
// ■ 🔴 **なぜグループの読み口が要るのか**
//   計画 `ADR-0036` D-06 は共有の単位を「個人**とグループ**」と定め、`ADR-0098` 決定 1 は共有先の
//   グループを **Keycloak のグループ ID** で持つと定めた。ID は改名・移動で共有が外れない鍵である
//   反面、**画面に出せる値ではない**（決定 1「画面には表示名を出し、識別子は出さない」）。
//   したがって ID ↔ 表示名を引く口が要る。決定 2 は「グループ UI は配線まで描かない」と留保したが、
//   本作業（`${current_groups}` の束縛）でその配線が入るため留保は解ける。
//
// ■ 認可は **ロール不問だが人の主体だけ**（`PlatformAuthPolicies.InteractiveUser`）
//   共有は所有者＝一般利用者の操作なのでロールでは絞らない（`ADR-0098` 決定 1）。
//   一方で `ADR-0100` フォローアップ 2 は**サービスアカウントも認証済みなので到達できる**ことを
//   実装側へ戻した —— 名簿の列挙を s2s の面へ出さない（[[IADR-0401]] 決定 2）分界は、
//   グループの名簿でも保つ。**`AdminOnly` を持つサービスアカウントも 403 である**
//   （ロールではなく主体の種別で分ける）。
//
// ■ 🔴 **面は 3 項目に閉じる**（`GroupSummaryDto`: ID・表示名・パス）
//   所属者・属性・ロールを運ぶ項目を型が持たない（`UserSummaryDto` と同じ作法）。
//   **グループの所属者を引く口は作らない** —— 作ると「誰がどのグループに居るか」が
//   全利用者から引ける名簿になる。
//
// ■ 🔴 **書き込みの口を作らない。** グループ木は管理者が Keycloak で作る（`ADR-0098` 決定 3）。
//
// ■ 2 つの口の違い（**片方で代用できない**。`UserLookupEndpoints` と同じ非対称）
//   - `lookup`: 名前の一部から**候補を探す**（木を平坦化してパス順）。
//   - `resolve`: 台帳にあるグループ ID を**表示名へ引く**。**無い ID は落ちる** ——
//     削除済みのグループへの共有は台帳に残るので、画面が「見つからないグループ」として
//     取り消しだけできるようにする。
public static class GroupLookupEndpoints
{
    public static IEndpointRouteBuilder MapGroupLookupEndpoints(this IEndpointRouteBuilder app)
    {
        // 🔴 **ロールは渡さない。人の主体だけを通す**（上の「認可」を参照）。
        var g = app.MapGroup("/authz/groups")
            .WithTags("GroupLookup")
            .RequireAuthorization(PlatformAuthPolicies.InteractiveUser);

        g.MapLookupGroups();
        g.MapResolveGroups();

        return app;
    }

    // RFC7807 準拠のバリデーションエラー（400）。`AuthzEndpoints` / `UserAdminEndpoints` と
    // **同じ形へ揃える**（画面が 3 種類の読み方を覚えなくて済む）。
    internal static IResult ValidationProblem(List<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["errors"] = errors.ToArray()
        });
}
