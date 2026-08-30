using GraphService.Features.Graph.CreateEdge;
using GraphService.Features.Graph.GetNode;
using GraphService.Features.Graph.Neighbors;

namespace GraphService.Features.Graph;

// FR-17, UC-10, ADR-0034: グラフ読み取りエンドポイントの合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/Graph/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるものだけ**である ——
// route group（`/graph`）と、**1 種類しかない 404**（`NotFound`）。
//
// 本単位（#908）が公開するのは**ホップ 0（起点ノード 1 件）**だけである。多ホップ探索・
// 上限 200/500・ホップ超過の拒否は #909 が足す。ホップ 0 でも認可の骨格はすべて通っており、
// deny-closed・存在秘匿・型ゲートがこの 1 本で実演される。
public static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/graph").WithTags("Graph");

        GetGraphNodeEndpoint.Map(g);
        GraphNeighborsEndpoint.Map(g);
        CreateGraphEdgeEndpoint.Map(g);

        return app;
    }

    // ADR-0034 決定 2: **1 種類の 404 しか返さない。**
    // 本文・ヘッダに差が出ると、そこから存在の有無が読めてしまう。
    // 🔴 **操作フォルダ側で `Results.NotFound()` を直に書かない。** 生成点を 1 つに保つことが、
    // 「区別できない 404」を構造的に守っている（GraphEndpointsSecrecyTests が固定する）。
    internal static IResult NotFound() => Results.NotFound();
}
