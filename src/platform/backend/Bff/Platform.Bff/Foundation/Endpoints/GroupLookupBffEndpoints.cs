using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Bff.Foundation.Endpoints;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, ADR-0100 フォローアップ 2,
// [[IADR-0447]] / [[IADR-0449]] (#1447):
// **共有先に指定するグループの検索・表示名の引き当て**の BFF 集約。
// AuthorizationService の `/authz/groups/lookup`・`/authz/groups/resolve` へ透過中継する。
//
// 🔴 **`/bff/users/*`（利用者の検索。`UserLookupBffEndpoints`）と同型・同じ認可である。**
//   画面（SC-19）は指定先の種別（個人／グループ）を切り替えるだけなので、**2 つの口の作法が
//   違うと画面側に分岐が生える**。中継の実体も同じヘルパ（`UserAdminBffEndpoints.Proxy`）を使う。
//
// 🔴 **認可はロール不問・ただし人の主体だけ**（`PlatformAuthPolicies.InteractiveUser`）。
//   共有は所有者＝一般利用者の操作なのでロールでは絞らない（`ADR-0098` 決定 1）が、
//   realm のサービスアカウントは通さない（`ADR-0100` フォローアップ 2 / [[IADR-0449]]）。
//   **実施点は後段でもある**（後段の群も同じポリシーを持つ。二重ゲート。[[IADR-0044]]）——
//   BFF は利用者の資格情報を転送する。転送を落とすと後段は 401 を返す
//   （**緩む向きではないが、機能が丸ごと死ぬ**）。
//
// 🔴 **書き込みの口を持たない。** グループ木は管理者が Keycloak で作る（`ADR-0098` 決定 3）。
public static class GroupLookupBffEndpoints
{
    public static IEndpointRouteBuilder MapGroupLookupBffEndpoints(this IEndpointRouteBuilder app)
    {
        // ADR-0098 決定 1 / ADR-0100 フォローアップ 2: ロールは要求しない。人の主体だけを通す。
        var g = app.MapGroup("/bff/groups")
            .WithTags("GroupLookup BFF")
            .RequireAuthorization(PlatformAuthPolicies.InteractiveUser);

        // SC-19 主要素 3: 候補の検索（`q` 2 文字以上・上限 50／既定 20・パス順）。
        // 🔴 **クエリ文字列はそのまま後段へ渡す** —— 既定・上限・検証の規則は後段が唯一の
        // 情報源であり（400 の本文もそこから来る）、BFF が写しを持つと 2 か所で食い違う。
        g.MapGet("/lookup", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            UserAdminBffEndpoints.Proxy(f, h, HttpMethod.Get,
                "/authz/groups/lookup" + h.Request.QueryString.Value, ct))
            .WithName("BffGroupLookup").Produces<List<GroupSummaryDto>>();

        // SC-19 主要素 3: 既存のグループ共有を表示名へ引く。**無い ID は落ちる**（エラーではない）。
        g.MapPost("/resolve", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            UserAdminBffEndpoints.Proxy(f, h, HttpMethod.Post, "/authz/groups/resolve", ct))
            .WithName("BffGroupResolve").Produces<List<GroupSummaryDto>>();

        return app;
    }
}
