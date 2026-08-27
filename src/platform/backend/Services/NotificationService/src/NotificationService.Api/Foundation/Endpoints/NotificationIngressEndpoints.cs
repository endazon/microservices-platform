using NotificationService.Api.Foundation.Contracts;
using NotificationService.Api.Foundation.Services;

namespace NotificationService.Api.Foundation.Endpoints;

// FR-22, ADR-0004, IADR-0215 決定 5, IADR-0270 決定 6: 通知の受け口（メッシュ内部限定）。
//
// **送信側は DocumentService の HttpPrivateNoteNotifier である**（発火の検知はデータの在る側で行う）。
// パスは送信側の宣言と同じ `/internal/notifications` —— platform → knowledge の参照は禁止のため
// 定数を共有できず、**文字列を複製して一致をテストで固定している**。
//
// ★ **認証を課さない。** 既存の内部 API（/internal/introspection・/internal/config/drift-run・
// /internal/mcp-tools）と同じ扱いである。呼び出し元は**ユーザー文脈を持たない定期処理**であり
// 利用者の JWT を持ち得ない —— `IADR-0017（Superseded by IADR-0026）` が正面から扱った制約そのもの。
// 第一防御は現行値としては mesh の STRICT mTLS（IADR-0026）で、ネットワーク分離（ホスト非公開・
// NetworkPolicy 既定拒否）は多層防御として存続する。
//
// 🔴 **残余リスク**: 同一ネットワーク内からは無認証で通知を作成できる。ただし**作れるのは件数・
// 閾値・期限だけを持つ通知**であり、**受け口は書き込み専用で既存の通知を読み出さない**
// （読み出しは認証必須の /notifications だけ）。**内部 API は OpenAPI にも載せない**
// （docs/api/openapi.yaml に /internal/* は 1 本も無い）。
public static class NotificationIngressEndpoints
{
    // 🔴 送信側 DocumentService.Api.Foundation.Services.HttpPrivateNoteNotifier.IngressPath と同値。
    public const string IngressPath = "/internal/notifications";

    public static IEndpointRouteBuilder MapNotificationIngressEndpoints(this IEndpointRouteBuilder app)
    {
        // RequireAuthorization() を付けない（上のコメントの理由）。
        app.MapPost(IngressPath, async (
            NotificationIngressRequest? request, NotificationIngress ingress, CancellationToken ct) =>
        {
            var outcome = await ingress.AcceptAsync(request, ct);

            // 不正なペイロードは 400。**1 件も永続化しない**（AcceptAsync が検証を先に行う）。
            if (!outcome.IsValid)
                return Results.ValidationProblem(outcome.Errors!);

            var body = new NotificationIngressResultDto(outcome.Id, outcome.IsDuplicate);

            // 200 = 同一事象の再送を畳んだ / 201 = 新規に受理した。
            // 送信側は成否しか見ないが、**畳んだことが観測できないと「届いていない」と区別できない。**
            return outcome.IsDuplicate
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status201Created);
        }).WithName("NotificationIngress")
          .ExcludeFromDescription();

        return app;
    }
}
