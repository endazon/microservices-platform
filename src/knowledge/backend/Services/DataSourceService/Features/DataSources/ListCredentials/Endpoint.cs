using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Features.DataSources.ListCredentials;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 1・3・4, [[IADR-0501]] 決定 3 (#458 段 S2):
// SC-22 の「データソースの資格情報」の群の**成員**（＝登録済みのデータソース）と、キーごとの供給の事実。
//
// - 群に入るのは**有効で**、コネクタが資格情報のキーを宣言するデータソースだけ（決定 1「無効化で一覧から消える」。
//   ファイルサーバーのように資格情報を使わない種別は項目を持たない）。
// - 🔴 **値も参照の文字列も返さない。** 返すのはキー名と 3 つの符号（`reference` / `other` / `absent`）だけである。
//   BFF はこの一覧で「登録済みの ID」を確かめてから Vault へ書く（決定 2。登録済みの ID はコードで限る）。
// - 認可はグループ既定（管理者・運用者）。運用者は群を閲覧できる（決定 3）。
internal static class ListDataSourceCredentialsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/credentials", async (DataSourceDbContext db, ConnectorRegistry connectors) =>
        {
            var sources = await db.DataSources
                .Where(ds => ds.Status == DataSourceStatus.Active)
                .OrderBy(ds => ds.CreatedAt)
                .ToListAsync();

            var items = new List<DataSourceCredentialItem>();
            foreach (var ds in sources)
            {
                var keys = connectors.Resolve(ds.SourceType)?.CredentialKeys ?? [];
                if (keys.Count == 0)
                    continue;
                items.Add(new DataSourceCredentialItem(
                    ds.Id, ds.Name, ds.SourceType,
                    [.. keys.Select(k => new DataSourceCredentialProperty(k, ds.CredentialSupplyOf(k)))]));
            }

            return Results.Ok(items);
        });
    }
}

// SC-22, [[IADR-0501]] 決定 3: 群の成員 1 件（BFF ↔ DataSourceService。Knowledge.Contracts の同名 DTO と JSON 互換）。
internal sealed record DataSourceCredentialItem(
    Guid Id, string Name, string SourceType, List<DataSourceCredentialProperty> Properties);

internal sealed record DataSourceCredentialProperty(string Name, string Supply);
