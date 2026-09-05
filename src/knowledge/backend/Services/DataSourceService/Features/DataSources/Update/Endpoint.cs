using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Kernel;

namespace DataSourceService.Features.DataSources.Update;

// FR-01, UC-04, SC-06（Q16 / #534）: 更新（全置換）。従前は更新の口が無く、登録済みソースの変更が
// 「削除→再登録」でしかできなかった。削除→再登録は **ID と履歴を切る**（認証情報のローテーションの
// たびに文書の出所の追跡が切れるのは監査上受け入れがたい）。
//
// **認可は管理者限定である**（計画 §SC-06「登録・更新・無効化は管理者限定」）。グループ既定は
// admin + operator なので、本エンドポイントだけ AdminOnly を上書きで要求する。
// **無効（disabled）なソースも更新できる** —— 無効化は論理削除であり、認証情報のローテーションは
// 無効中にも起こる。
internal static class UpdateDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPut("/{id:guid}", async (Guid id, UpdateDataSourceRequest req,
            IValidator<UpdateDataSourceRequest> validator, DataSourceDbContext db,
            SyncSchedule schedule, IPlatformUserDirectory userDirectory, CancellationToken ct) =>
        {
            // FR-01, UC-04 / IADR-0371 決定 2・4 / IADR-0395: 入力検証（FluentValidation）の失敗を
            // Kernel の `Result` で表し、**HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // 🔴 **判定の位置は移送前のガード節と同じ（`FindAsync` より前）である** ——
            // 省略のある PUT は、対象が不存在でも 400 が返る（404 ではない）。
            var gate = Validate(validator, req);
            if (gate.IsFailure)
                return Results.BadRequest(new { error = gate.Error.Message });

            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            // IADR-0295 決定 3: **既存値を渡す。** 応答のマスク済みの値をそのまま書き戻した形は
            // 受理し（`Update` が実値を保つ）、編集して送り返した形は弾く。
            if (ConnectionUriPolicy.Validate(req.ConnectionUri, ds.ConnectionUri) is { } uriError)
                return Results.BadRequest(new { error = uriError });

            // FR-05, SC-06, ADR-0074 決定 4 (#1194): 写像先の実在をサーバ側で検証する。
            if (await OwnerMappingValidation.ValidateAsync(req.OwnerMappings, userDirectory, ct) is { } mapError)
                return mapError;

            ds.Update(req.Name, req.SourceType, req.ConnectionUri, req.Config, req.DefaultAttributes,
                req.OwnerMappings);
            await db.SaveChangesAsync();
            return Results.Ok(DataSourceEndpoints.ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }

    // FR-01 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `UpdateDataSourceValidator` が持つ。**
    // 規則は 1 本だが、`Errors[0]` を採る形は他の端点と揃える（規則が増えたときに
    // 「宣言順が応答の契約」という読み方がそのまま効く）。
    private static Result Validate(IValidator<UpdateDataSourceRequest> validator,
        UpdateDataSourceRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation(
                "datasource.update.invalid", result.Errors[0].ErrorMessage));
    }
}
