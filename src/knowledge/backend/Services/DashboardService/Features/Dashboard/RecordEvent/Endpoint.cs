using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Platform.Shared.Kernel;

namespace DashboardService.Features.Dashboard.RecordEvent;

// FR-10: 利用イベント（検索・回答）を記録する。
// 集計の入力となるため、認証済みなら誰でも記録できる（管理者限定にはしないが、認証は必須）。
//
// 🔴 **記録する主体は列へ書かない**（ADR-0072 決定 1・[[IADR-0367]] (#1198)）。
// 変わったのは「解決した主体を列へ書くこと」だけであり、**`RequireAuthorization()` は維持する**
// —— 認証は不正投入の統制であり、記録の統制とは別である（案 a の却下理由）。
// 一緒に外すと誰でも利用イベントを投げられる。
internal static class RecordUsageEventEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/events", async (UsageEventRequest req, IValidator<UsageEventRequest> validator,
            DashboardDbContext db, IOptions<SyntheticMonitoringOptions> synthetic, HttpContext http,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            // NFR-02, ADR-0071, ADR-0072, ADR-0076 決定 4, [[IADR-0378]] (#1203):
            // 🔴 **多層防御の 2 枚目。** 1 枚目は BFF（`IUsageEventReporter` の入口）であり、
            // 通常の経路はそこで落ちてここへ来ない。ここが要るのは**受け口を直接叩かれた場合**であり、
            // 除外を「1 つの呼び出し元の作法」ではなく**行を作る側の性質**にするためである
            // （[[IADR-0044]] の多層防御・[[IADR-0039]] 決定 2 と同じ向き）。
            //
            // 🔴 **判定は検証済み JWT の主体だけを見る。** ここも外部から到達し得る面ではないが、
            // `RequireAuthorization()` を持つ以上、判定材料を主体に揃える（外周と内周で規則を割らない）。
            //
            // 応答は **202 Accepted**（201 ではない）。「受け取ったが行は作っていない」を状態で表す。
            // 400 にしないのは、これが**誤りではなく設計どおりの除外**だからである。
            if (SyntheticTraffic.IsSyntheticPrincipal(http.User, synthetic.Value))
            {
                // 🔴 **本文の値をそのままログへ出さない。** `req.EventType` は要求本文由来であり、
                // ここは検証（`IsValid`）より前なので改行や制御文字を含み得る。素通しすると
                // 偽の行を差し込めてしまう（ログ注入）。既知の値集合へ畳んでから出す
                // —— 知りたいのは「どの種別で来たか」であって原文ではない（[[IADR-0306]] の
                // ログ衛生と同じ向き）。
                // `Normalize` は小文字化するだけで、値は要求本文から導かれたままである
                // （改行は残る）。**分岐で定数リテラルそのものを選ぶ**ことで、ログへ渡る値を
                // コンパイル時に決まる 3 つへ閉じる。
                var loggedType = !UsageEventType.IsValid(req.EventType) ? "(invalid)"
                    : string.Equals(req.EventType, UsageEventType.Search, StringComparison.OrdinalIgnoreCase)
                        ? UsageEventType.Search
                        : UsageEventType.Answer;
                loggerFactory.CreateLogger(typeof(RecordUsageEventEndpoint)).LogWarning(
                    "合成監視の主体から利用イベントが直接投入された。行は作らない（ADR-0076 決定 4）。"
                    + "eventType={EventType}。検索語と利用者は本文へ出さない。"
                    + "**通常の経路（BFF）は発火前に落とすため、ここへ到達するのは想定外である。**",
                    loggedType);
                return Results.Accepted();
            }

            // FR-10 / IADR-0371 決定 2・4 / IADR-0393: 入力検証（FluentValidation）の失敗を
            // Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // 返す状態コードも本文も移送前と変わらない。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
            {
                // ErrorKind で HTTP を決める。**分岐は 1 箇所に閉じる。**
                return gate.Error.Kind == ErrorKind.Validation
                    ? Results.BadRequest(new { error = gate.Error.Message })
                    : Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            var type = UsageEventType.Normalize(req.EventType);
            // 検索語は種別が search のときのみ意味を持つ（answer では保持しない）。
            var query = type == UsageEventType.Search ? Normalize(req.Query) : null;

            var ev = UsageEvent.Create(type, query);
            db.UsageEvents.Add(ev);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/dashboard/events/{ev.Id}", new { ev.Id });
        }).WithName("RecordUsageEvent").RequireAuthorization().Produces(StatusCodes.Status201Created);
    }

    // FR-10 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `RecordUsageEventValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    private static Result Validate(IValidator<UsageEventRequest> validator, UsageEventRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation("dashboard.event.invalid", result.Errors[0].ErrorMessage));
    }

    // 検索語の集計キーを安定させるため、前後空白を除去し小文字化する（空は null 扱い）。
    // **記録だけが使う**ため 3 段目に置く（ADR-0068 決定 2）。
    private static string? Normalize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return query.Trim().ToLowerInvariant();
    }
}
