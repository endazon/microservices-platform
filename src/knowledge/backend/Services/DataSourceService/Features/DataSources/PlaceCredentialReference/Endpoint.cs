using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DataSourceService.Features.DataSources.PlaceCredentialReference;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 1・3・4, [[IADR-0501]] 決定 3 (#458 段 S2):
// SC-22 で Vault（`datasource/<ID>`）へ書いた**後に**、BFF が呼んで正規の参照を `Config` へ置く。
//
// - 🔴 **値を受け取らない。** 本文は無く、置くのは `vault:datasource/<自分の ID>#<キー>` だけ —— データソースの API が
//   秘密を受け取る形（[[IADR-0403]] 決定 8 段 1）は採らない（ADR-0126 決定 1。投入の面は SC-22）。
// - **値を持たないキーにだけ置く。** 平文（`other`）は置き換えない（移送は段 S4。決定 4「画面以外」）。
// - **管理者だけ**（決定 3。SC-06 の更新と同じ）。BFF の群の書き込みも管理者だけで、後段にも同じ制限を積む多層防御である（[[IADR-0044]]）。
// - 無効・未登録・資格情報を使わない種別は 404、コネクタが宣言しないキーは 400。
internal static class PlaceCredentialReferenceEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPut("/{id:guid}/credentials/{key}/reference", async (
            Guid id, string key, DataSourceDbContext db, ConnectorRegistry connectors, CancellationToken ct) =>
        {
            var ds = await db.DataSources.FindAsync([id], ct);
            if (ds is null || ds.Status != DataSourceStatus.Active)
                return Results.NotFound();

            var keys = connectors.Resolve(ds.SourceType)?.CredentialKeys ?? [];
            if (keys.Count == 0)
                return Results.NotFound();
            if (!keys.Contains(key, StringComparer.Ordinal))
                return Results.BadRequest(new { error = "このキーはコネクタの資格情報として宣言されていません。" });

            var supply = ds.PlaceCredentialReference(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { property = key, supply });
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
