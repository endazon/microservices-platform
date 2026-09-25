using System.Security.Claims;
using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources.Create;

// FR-01, SC-06（#628）: 登録は**管理者限定**である（計画 §SC-06「登録・更新・無効化は管理者限定」・
// 裁定 Q19「破壊的操作は管理者限定を維持する」）。グループ既定（admin ＋ operator）は
// **閲覧の下限**を表すので残し、本エンドポイントだけ AdminOnly を積む（AND 合成で実効 admin のみ。
// [[IADR-0128]] 決定 1 が #501 で確立した形）。
internal static class CreateDataSourceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (CreateDataSourceRequest req, DataSourceDbContext db, SyncSchedule schedule,
            IPlatformUserDirectory userDirectory, HttpContext http, CancellationToken ct) =>
        {
            // IADR-0295 決定 3: 資格情報つきの connectionUri は受け付けない（登録時が第 1 の関門）。
            if (ConnectionUriPolicy.Validate(req.ConnectionUri, existing: null) is { } uriError)
                return Results.BadRequest(new { error = uriError });

            // FR-05, SC-06, ADR-0074 決定 4 (#1194): 写像先の実在をサーバ側で検証する。
            // **通らない対は保存しない** —— 誤った写像は偽の所有者を作り、ADR-0036 の
            // 裁量制御が意図しない相手に開く。
            if (await OwnerMappingValidation.ValidateAsync(req.OwnerMappings, userDirectory, ct) is { } mapError)
                return mapError;

            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            // FR-05, UC-04, SC-06, IADR-0468 (#754): `department` が未指定なら、**登録した管理者の
            // 部門グループ**（ちょうど 1 つのとき）で補う。導けなければ従来どおり予約値 `unassigned`。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes, req.OwnerMappings,
                registrantDepartment: RegistrantDepartmentOf(http.User));
            db.DataSources.Add(ds);
            await db.SaveChangesAsync();
            return Results.Created($"/datasources/{ds.Id}",
                DataSourceEndpoints.ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }

    // FR-05, UC-04, SC-06, ADR-0109 決定 3, IADR-0468 (#754): 登録者の所属グループの**フルパス**を運ぶクレーム。
    // realm の `abac-attributes` スコープの `group-paths` マッパー（`oidc-group-membership-mapper`・
    // `full.path: true`）が発行する。**マッパーが無い realm ではクレームが無く、何も導かない**（安全側）。
    //
    // 🔴 **`groups` クレームは使わない。** あちらは `full.path: false`（名前だけ）で、
    // `/department/sales` と `/teams/sales` を区別できない。`department` クレームも使わない ——
    // 利用者属性が優先され、グループ由来でも複数所属を 1 つへ黙って畳むので「ちょうど 1 つ」を判定できない。
    //
    // **このトークンは本サービスが自ら検証している**（`AddPlatformAuth`。BFF が中継した利用者の資格情報）。
    // 登録を許可した `AdminOnly` の判定も同じトークンのクレームに依っており、所属を同じトークンから
    // 読んでも信頼境界は増えない。
    internal const string GroupPathsClaim = "group_paths";

    // 配列クレームは .NET で**同じ型の複数クレーム**になる（`FindFirst` では先頭 1 つに畳まれる）。
    internal static string? RegistrantDepartmentOf(ClaimsPrincipal user) =>
        RegistrantDepartment.FromGroupPaths(user.FindAll(GroupPathsClaim).Select(c => c.Value));
}
