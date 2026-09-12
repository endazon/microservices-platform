using Platform.Shared.Contracts.Dtos;

namespace Platform.Bff.Foundation.Endpoints;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
// **共有先に指定する利用者の検索・表示名の引き当て**の BFF 集約。
// AuthorizationService の `/authz/users/lookup`・`/authz/users/resolve` へ透過中継する。
//
// 🔴 **管理面（`/bff/admin/users`。`UserAdminBffEndpoints`）とは別の口である。**
//   あちらは **AdminOnly** で、ロール・ABAC 属性つきの広い像（`PlatformUserDto`）を返す。
//   こちらは **認証のみ・ロール不問**で、返すのは利用者名・表示名・有効状態の 3 つだけ
//   （`UserSummaryDto`）である。**prefix ごと分けてある** —— 1 つの口に両方を担わせると、
//   一般利用者が 403 になるか、名簿と属性が漏れるかのどちらかになる
//   （辞書管理と描画用カタログを分けた `/bff/graph/edge-types` と同じ切り分け）。
//
// 🔴 **認可の実施点は後段である。** 後段の群も `RequireAuthorization()`（ロール不問）を持ち、
//   BFF は利用者の資格情報を転送する（二重ゲート。[[IADR-0044]]）。転送を落とすと後段は
//   401 を返す —— **緩む向きではないが、機能が丸ごと死ぬ**。
//
// 🔴 **書き込みの口を持たない。** 利用者の作成・変更は計画が本画面から禁じており（SC-17）、
//   この群が担うのは読み取り 2 つだけである。
public static class UserLookupBffEndpoints
{
    public static IEndpointRouteBuilder MapUserLookupBffEndpoints(this IEndpointRouteBuilder app)
    {
        // ADR-0098 決定 1: 共有は一般利用者（所有者）の操作である。**ロールを要求しない。**
        var g = app.MapGroup("/bff/users")
            .WithTags("UserLookup BFF")
            .RequireAuthorization();

        // SC-19 主要素 3: 候補の検索（`q` 2 文字以上・有効な利用者のみ・上限 50／既定 20）。
        // 🔴 **クエリ文字列はそのまま後段へ渡す** —— 既定・上限・検証の規則は後段が唯一の
        // 情報源であり（400 の本文もそこから来る）、BFF が写しを持つと 2 か所で食い違う。
        g.MapGet("/lookup", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            UserAdminBffEndpoints.Proxy(f, h, HttpMethod.Get,
                "/authz/users/lookup" + h.Request.QueryString.Value, ct))
            .WithName("BffUserLookup").Produces<List<UserSummaryDto>>();

        // SC-19 主要素 3: 既存の共有先を表示名へ引く。**居ない名前は落ちる**（エラーではない）。
        // **無効化済みも返る**（`enabled=false` で画面が区別する）。
        g.MapPost("/resolve", (IHttpClientFactory f, HttpContext h, CancellationToken ct) =>
            UserAdminBffEndpoints.Proxy(f, h, HttpMethod.Post, "/authz/users/resolve", ct))
            .WithName("BffUserResolve").Produces<List<UserSummaryDto>>();

        return app;
    }
}
