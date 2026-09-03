using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

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
        g.MapPut("/{id:guid}", async (Guid id, UpdateDataSourceRequest req, DataSourceDbContext db,
            SyncSchedule schedule, IPlatformUserDirectory userDirectory, CancellationToken ct) =>
        {
            // AI レビュー 🟡（#627）: **省略を受理しない。** PUT は全置換なので「省略 ＝ 空で置換」は
            // 意味論としては筋が通るが、契約が省略を許していると**うっかりで秘密が消える**
            // （`config` を送り忘れた PUT が apiToken を丸ごと落とす）。消したいなら `{}` と明示させる。
            // **null を現状維持にする道は採らない** —— それをやると PUT と PATCH の区別が消える。
            if (req.Config is null || req.DefaultAttributes is null)
                return Results.BadRequest(new
                {
                    error = "PUT は全置換です。config と defaultAttributes を明示してください"
                          + "（消す場合は {} を送る）。一部だけ変更するなら PATCH を使ってください。",
                });

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
}
