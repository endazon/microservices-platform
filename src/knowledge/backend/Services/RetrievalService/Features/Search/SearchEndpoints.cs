using RetrievalService.Features.Search.AttributeValues;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Features.Search;

// FR-03, FR-04, UC-01: 検索集約の登録表（ADR-0068 決定 1）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/Search/<操作>/` に居る（ADR-0065 決定 2）。
// **`Program.cs` から呼ぶメソッド名とシグネチャは変えない** —— ルート登録順・タグ付け・
// フィルタ適用順が動かないことを、この形で担保する（ADR-0068 決定 1）。
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/search").WithTags("Search");

        SearchEndpoint.Map(g);
        AttributeValuesEndpoint.Map(g);

        return app;
    }
}
