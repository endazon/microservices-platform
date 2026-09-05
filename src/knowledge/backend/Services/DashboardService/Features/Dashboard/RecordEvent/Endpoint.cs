using DashboardService.Domain;
using DashboardService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
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
            DashboardDbContext db, CancellationToken ct) =>
        {
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
