using NotificationService.Domain;

namespace NotificationService.Features.Notifications.ListNotifications;

// FR-22, IADR-0215: 本人宛の通知一覧（新しい順）と未読件数（GET /notifications）。
public static class ListNotificationsEndpoint
{
    public static IEndpointRouteBuilder MapListNotifications(this IEndpointRouteBuilder app)
    {
        app.MapGet("", async (
            HttpContext http, NotificationStore store,
            bool? unreadOnly, int? limit, CancellationToken ct) =>
        {
            // ★ 主体はトークンからしか採らない。クエリ・本文に主体の口を作っていない。
            var subject = NotificationSubject.Of(http.User);
            if (subject is null)
                return Results.Unauthorized();

            return Results.Ok(await store.ListAsync(subject, unreadOnly ?? false, limit, ct));
        });

        return app;
    }
}
