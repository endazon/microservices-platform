using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.EdgeTypes.Catalog;

// FR-17, SC-18 (#962): **描画用カタログ。認証のみでロールを問わない。**
//
// 🔴 **使用件数つきの一覧（`ListEdgeTypesEndpoint`）を一般利用者へ開けてはならない。**
// `UsageCount` は全辺を数えており ABAC で絞られていない。ホップごと ABAC（IADR-0242）が
// 個々のノード・辺を隠しているのに、**集計値が総量を漏らす**。だから件数を含まない別の口を置く。
//
// 🔴 **ロール要求を足さないこと。** SC-18 は一般利用者の画面であり、辺の型名が引けないと
// 辺の描き分けも型フィルタも描けない（グラフ応答が返すのは `EdgeTypeId` だけである）。
// **`read` / `write` の group に載せないのは、その group の既定がロールを要求するからである。**
internal static class ListEdgeTypeCatalogEndpoint
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/graph/edge-types/catalog", async (GraphDbContext db, CancellationToken ct) =>
            Results.Ok(await LoadCatalogAsync(db, ct)))
            .WithTags("EdgeTypes").RequireAuthorization()
            .WithName("ListEdgeTypeCatalog").Produces<List<EdgeTypeCatalogItemDto>>();
    }

    // FR-17, SC-18 (#962): 描画用カタログ。**辺を一切参照しない** ——
    // 参照しないことが、集計値を漏らさないことの構造的な保証である。
    private static async Task<List<EdgeTypeCatalogItemDto>> LoadCatalogAsync(
        GraphDbContext db, CancellationToken ct)
        => await db.EdgeTypes.AsNoTracking()
            .OrderBy(t => t.Name)
            // FR-04, ADR-0035 決定 2 (#970): `Weight` は二段検索の再ランクが引く（#947a が入れた値の公開面）。
            .Select(t => new EdgeTypeCatalogItemDto(t.Id, t.Name, t.Layer, t.IsSymmetric, t.Weight))
            .ToListAsync(ct);
}
