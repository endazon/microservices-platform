using NotificationService.Api.Foundation.Services;

namespace NotificationService.Api.Foundation.Endpoints;

// FR-22, IADR-0215: アプリ内通知のエンドポイント。
//
// **BFF の `/bff/notifications*` の後段である。** 面の形（クエリ・状態コード・応答スキーマ）は
// 通信仕様書（docs/api/BFF_notifications.md）と openapi.yaml に合わせてある。
//
// ★ **認証は必須だがロールは問わない**（契約の `x-roles: []`）。**絞るのは役割ではなく主体**
// （JWT の sub）である —— 通知は所有者本人だけのものであり、管理者であっても他人の通知は読めない。
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // RequireAuthorization() は「認証済みであること」だけを要求する（ロール要件を足さない）。
        var g = app.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();

        // FR-22: 本人宛の通知一覧（新しい順）と未読件数。
        g.MapGet("", async (
            HttpContext http, NotificationStore store,
            bool? unreadOnly, int? limit, CancellationToken ct) =>
        {
            // ★ 主体はトークンからしか採らない。クエリ・本文に主体の口を作っていない。
            var subject = NotificationSubject.Of(http.User);
            if (subject is null)
                return Results.Unauthorized();

            return Results.Ok(await store.ListAsync(subject, unreadOnly ?? false, limit, ct));
        });

        // FR-22: 通知 1 件の既読化。**冪等**（既読へもう一度呼んでも 200）。
        g.MapPost("/{id:guid}/read", async (
            Guid id, HttpContext http, NotificationStore store, CancellationToken ct) =>
        {
            var subject = NotificationSubject.Of(http.User);
            if (subject is null)
                return Results.Unauthorized();

            var result = await store.MarkReadAsync(subject, id, ct);

            // ★ **本人の通知でなければ 404**。「存在しない」と「本人のものでない」を区別しない
            //   （存在秘匿。403 を返すと他人の通知 ID の実在が漏れる）。
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return app;
    }
}
