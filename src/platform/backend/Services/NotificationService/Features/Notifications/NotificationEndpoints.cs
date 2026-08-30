using NotificationService.Features.Notifications.ListNotifications;
using NotificationService.Features.Notifications.MarkRead;

namespace NotificationService.Features.Notifications;

// FR-22, IADR-0215: アプリ内通知スライスの合成点。
//
// **BFF の `/bff/notifications*` の後段である。** 面の形（クエリ・状態コード・応答スキーマ）は
// 通信仕様書（docs/api/BFF_notifications.md）と openapi.yaml に合わせてある。
//
// ★ **認証は必須だがロールは問わない**（契約の `x-roles: []`）。**絞るのは役割ではなく主体**
// （JWT の sub）である —— 通知は所有者本人だけのものであり、管理者であっても他人の通知は読めない。
// **この扱いが 2 操作で同じであることを、グループ 1 箇所で表す**（ADR-0065 決定 2）。
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // RequireAuthorization() は「認証済みであること」だけを要求する（ロール要件を足さない）。
        var g = app.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization();

        g.MapListNotifications();
        g.MapMarkNotificationRead();

        return app;
    }
}
