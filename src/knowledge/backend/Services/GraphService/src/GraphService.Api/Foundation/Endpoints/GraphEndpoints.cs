using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;

namespace GraphService.Api.Foundation.Endpoints;

// FR-17, UC-10, ADR-0034: グラフ読み取りエンドポイント。
//
// 本単位（#908）が公開するのは**ホップ 0（起点ノード 1 件）**だけである。多ホップ探索・
// 上限 200/500・ホップ超過の拒否は #909 が足す。ホップ 0 でも認可の骨格はすべて通っており、
// deny-closed・存在秘匿・型ゲートがこの 1 本で実演される。
public static class GraphEndpoints
{
    public static IEndpointRouteBuilder MapGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/graph").WithTags("Graph");

        // FR-17, UC-10, ADR-0034 決定 1・2: 起点ノードを 1 件返す。
        //
        // 🔴 **非許可・レコード欠落・文書不存在をすべて同一の 404 に倒す。**
        // 403 と 404 を打ち分けると存在が漏れる。ADR-0034 は「利用者がリンク切れと権限不足を
        // 区別できないこと」を**受け入れ済みの副作用**として明記しており、本実装はその線に従う
        // （GraphEndpointsSecrecyTests が本文・ヘッダで区別できないことを固定する）。
        g.MapGet("/{documentId:guid}", async (
            Guid documentId,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            HttpContext http,
            CancellationToken ct) =>
        {
            // ADR-0034: スコープはリクエストごとに 1 回だけ解決する。
            // 認可サービス障害時は Granted=false へ縮退する（GraphAccessResolver）。
            var scope = await accessResolver.ResolveAsync(http, ct);

            // FR-05: deny-by-default。許可ポリシーが無ければ起点自体も見せない。
            if (!scope.Granted)
                return NotFound();

            var node = await store.FindNodeAsync(documentId, ct);

            // 「文書が無い」と「属性の複製がまだ届いていない」は同じ扱いになる
            // （IADR-0239 決定 12-3: 複製が無いノードは不可視）。
            if (node is null)
                return NotFound();

            // 🔴 **ここが唯一の構築経路である。** 非許可なら AuthorizedNode は作れない。
            var authorized = AuthorizedNode.Authorize(node, scope);
            if (authorized is null)
                return NotFound();

            // 本単位は辺を返さない（多ホップ探索は #909）。
            var subgraph = new UnfilteredSubgraph([authorized.Node], [], Truncated: false);

            // 🔴 **応答 DTO は Seal からしか作れない。** 未フィルタ結果は直列化できない。
            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNode")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    // ADR-0034 決定 2: **1 種類の 404 しか返さない。**
    // 本文・ヘッダに差が出ると、そこから存在の有無が読めてしまう。
    private static IResult NotFound() => Results.NotFound();
}
