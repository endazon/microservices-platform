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
            IPlatformUserDirectory userDirectory, IDepartmentDomainDirectory departmentDomain, HttpContext http,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            // IADR-0295 決定 3: 資格情報つきの connectionUri は受け付けない（登録時が第 1 の関門）。
            if (ConnectionUriPolicy.Validate(req.ConnectionUri, existing: null) is { } uriError)
                return Results.BadRequest(new { error = uriError });

            // FR-05, SC-06, ADR-0074 決定 4 (#1194): 写像先の実在をサーバ側で検証する。
            // **通らない対は保存しない** —— 誤った写像は偽の所有者を作り、ADR-0036 の
            // 裁量制御が意図しない相手に開く。
            if (await OwnerMappingValidation.ValidateAsync(req.OwnerMappings, userDirectory, ct) is { } mapError)
                return mapError;

            // FR-05, SC-06, 計画 ADR-0115 決定 5, [[IADR-0472]] (#1557): 明示した部門が値域（realm の部門グループ）に
            // 在ることをサーバ側で検証する。未指定・空白・予約値 `unassigned` は照会しない（下の導出に委ねる）。
            // 🔴 **登録者の部門から導いた値は検証しない** —— 導出は `/department/<code>` のフルパスからしか作らず、
            // 構成上つねに値域の内側である（IADR-0468）。
            if (await DepartmentDomainValidation.ValidateAsync(req.DefaultAttributes, departmentDomain, ct) is { } deptError)
                return deptError;

            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            // FR-05, UC-04, SC-06, IADR-0468 (#754): `department` が未指定なら、**登録した管理者の
            // 部門グループ**（ちょうど 1 つのとき）で補う。導けなければ従来どおり予約値 `unassigned`。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes, req.OwnerMappings,
                registrantDepartment: ResolveRegistrantDepartment(
                    http.User, req.DefaultAttributes, loggers.CreateLogger(LogCategory)));
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

    internal const string LogCategory = "DataSourceService.Features.DataSources.Create";

    // FR-05, UC-04, SC-06, IADR-0468 決定 6 (#754 監査): 導けなかった理由を**区別してログに残す**。
    //
    // 保存される値はどちらも予約値 `unassigned` だが、**原因と直し方が違う**:
    //   - クレーム自体が無い（Warning）: realm の `group-paths` マッパーが未適用（`reconcile-realm.sh` 前）の疑い。
    //     直すのは運用者。🔴 **Keycloak は所属が空の登録者についてこのクレームを省き得る**（空の複数値を発行しない挙動。
    //     実機では未確認）。その場合トークンだけでは「マッパー未適用」と「所属 0」を区別できない —— 文言に両方の原因を書く。
    //   - クレームはあるが部門グループがちょうど 1 つではない（Information）: 0 個か 2 個以上。設計どおりの「導かない」。
    // 🔴 **1 回の登録につき 1 行だけ**出す。`department` を明示した登録（導く必要が無い）では何も出さない。
    // 利用者識別子・グループ名は載せない（件数だけ）。
    internal static string? ResolveRegistrantDepartment(
        ClaimsPrincipal user, IReadOnlyDictionary<string, string>? requested, ILogger logger)
    {
        if (!DataSource.IsDepartmentUnresolved(requested)) return null;

        var paths = user.FindAll(GroupPathsClaim).Select(c => c.Value).ToList();
        if (paths.Count == 0)
        {
            logger.LogWarning(
                "登録者のトークンに {Claim} クレームが無いため、データソースの部門を導けない（予約値 unassigned にする）。"
                + "realm の group-paths マッパーがまだ適用されていない（reconcile-realm 前）か、"
                + "登録者がどのグループにも属していない（所属が空だと Keycloak がクレームを省き得る）。",
                GroupPathsClaim);
            return null;
        }

        var code = RegistrantDepartment.FromGroupPaths(paths);
        if (code is null)
        {
            var departmentPaths = paths.Count(p => p.StartsWith(RegistrantDepartment.DepartmentGroupRoot, StringComparison.Ordinal));
            logger.LogInformation(
                "{Claim} クレームはあるが、登録者の部門グループがちょうど 1 つではない（部門配下の所属 {DepartmentPaths} 件）。"
                + "データソースの部門は導かず予約値 unassigned にする。",
                GroupPathsClaim, departmentPaths);
        }
        return code;
    }
}
