using NotificationService.Domain;

namespace NotificationService.Features.Notifications.MarkRead;

// FR-22, IADR-0215: 通知 1 件の既読化（POST /notifications/{id}/read）。**冪等**（既読へもう一度呼んでも 200）。
public static class MarkNotificationReadEndpoint
{
    public static IEndpointRouteBuilder MapMarkNotificationRead(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{id:guid}/read", async (
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
