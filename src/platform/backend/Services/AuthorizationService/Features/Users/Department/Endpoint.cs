using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.Department;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0116 決定 1, [[IADR-0473]] (#1610): 利用者の部門（部門グループの所属）の読み取りと変更。
// 🔴 **利用者属性 `department` を書かない**（部門の同期が追いつく）。書くのは部門グループの所属だけである（`UserDepartmentService`）。
//
// 状態コード: 200（読み取り・変更後の所属）／400（realm の部門グループに無いコード）／404（利用者が居ない）／
// 409（2 個以上の部門グループ・書いた後の読み直しが期待と違う）／502（所属の変更が途中で失敗した。補償の結果つき）／
// 503（realm を読めない。何も変えていない）。理由は問題詳細の `detail`（画面がそのまま出す）。
public static class UserDepartmentEndpoint
{
    public static IEndpointRouteBuilder MapUserDepartment(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{userId}/department", async (
            string userId, UserDepartmentService service, CancellationToken ct) =>
            ToResult(await service.ReadAsync(userId, ct)));

        app.MapPut("/{userId}/department", async (
            string userId, ReplaceUserDepartmentRequest req, UserDepartmentService service, CancellationToken ct) =>
            ToResult(await service.ChangeAsync(userId, req.Department, ct)));

        return app;
    }

    private static IResult ToResult(UserDepartmentOutcome outcome) => outcome.Kind switch
    {
        UserDepartmentOutcomeKind.Ok => Results.Ok(outcome.Value),
        UserDepartmentOutcomeKind.NotFound => Results.NotFound(),
        UserDepartmentOutcomeKind.Invalid => UserAdminEndpoints.ValidationProblem([outcome.Message!]),
        UserDepartmentOutcomeKind.Conflict => Results.Problem(outcome.Message, statusCode: StatusCodes.Status409Conflict),
        UserDepartmentOutcomeKind.Failed => Results.Problem(outcome.Message, statusCode: StatusCodes.Status502BadGateway),
        UserDepartmentOutcomeKind.RealmUnavailable =>
            Results.Problem(outcome.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => throw new InvalidOperationException($"未知の結末: {outcome.Kind}"),
    };
}
