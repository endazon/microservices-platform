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
            // （IADR-0242 決定 12-3: 複製が無いノードは不可視）。
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

        // FR-17, UC-10, ADR-0034 決定 1・2・3・4: 近傍探索（多ホップ）。
        //
        // 🔴 **hops 上限の超過は 400 で拒否する。黙って切り詰めない**（決定 3）。
        // 切り詰めると、利用者は「3 ホップ先まで見た」と思い込んだまま欠けた結果を受け取る。
        g.MapGet("/{documentId:guid}/neighbors", async (
            Guid documentId,
            int? hops,
            IGraphAccessResolver accessResolver,
            IGraphStore store,
            GraphTraversal traversal,
            HttpContext http,
            CancellationToken ct) =>
        {
            // 🔴 **hops の検証は認可より前に置く。順序を入れ替えてはならない。**
            //
            // 入れ替えると存在秘匿が壊れる —— 認可を先にすると、権限外・不存在の文書は 404、
            // 可視の文書だけが 400 を返すようになり、**hops=4 を投げるだけで文書の存在が判る**。
            // hops の妥当性は文書に依存しないので、先に弾けば何も漏れない。
            //
            // ⚠ CodeQL の `cs/user-controlled-bypass` がここを high で指摘する（利用者入力が
            // 条件を制御しているため）。**バイパスではない** —— この分岐は要求を*拒否*するだけで、
            // 通過した場合の認可（スコープ解決・Authorize）は無条件に実行される。
            // 指摘に従って順序を変えると、上記のとおり実際の情報漏れを作ることになる。
            var requested = hops ?? GraphTraversal.DefaultHops;
            if (requested < 1 || requested > GraphTraversal.MaxHops)
                return Results.BadRequest(new
                {
                    error = "hops_out_of_range",
                    message = $"hops は 1〜{GraphTraversal.MaxHops} で指定する（既定 {GraphTraversal.DefaultHops}）。",
                });

            var scope = await accessResolver.ResolveAsync(http, ct);
            if (!scope.Granted)
                return NotFound();

            var start = await store.FindNodeAsync(documentId, ct);
            if (start is null)
                return NotFound();

            var origin = AuthorizedNode.Authorize(start, scope);
            if (origin is null)
                return NotFound();

            var subgraph = await traversal.ExploreAsync(origin, scope, requested, ct);

            return Results.Ok(GraphViewResponse.Seal(subgraph, scope));
        }).WithName("GetGraphNeighbors")
          .RequireAuthorization()
          .Produces<GraphViewResponse>()
          .Produces(StatusCodes.Status400BadRequest)
          .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    // ADR-0034 決定 2: **1 種類の 404 しか返さない。**
    // 本文・ヘッダに差が出ると、そこから存在の有無が読めてしまう。
    private static IResult NotFound() => Results.NotFound();
}
