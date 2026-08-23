using RetrievalService.Api.Foundation.Ports;
using System.Net;
using System.Net.Http.Json;

namespace RetrievalService.Api.Composable.Adapters;

// FR-04, FR-17, UC-10, ADR-0034, ADR-0035 決定 2 (#970): 近傍展開ポートの GraphService 実装。
//
// 🔴 **権限伝播は `Authorization` ヘッダの伝播（方式 A）である**（#916a で確立した規則
// 「下流が自分で ABAC を解決する型なら A」）。GraphService は `GraphAccessResolver` で
// JWT から自分でスコープを解決し、ホップごとに判定する（IADR-0242）。
// **解決済み scope を本文で渡す方式 B を採ってはならない** —— 採ると GraphService に
// 「本文で渡された scope を信じる」口が開き、そこへ到達できる誰もが任意の scope を主張できる。
public sealed class GraphServiceNeighborExpander(
    IHttpClientFactory httpFactory,
    IHttpContextAccessor accessor,
    ILogger<GraphServiceNeighborExpander> logger)
    : IGraphNeighborExpander
{
    public const string ClientName = "GraphService";

    // 🔴 **辺の型の重みは GraphService のどの口からも取れない**（実測。2026-08-23）。
    // #947a が `edge_types.Weight` を入れたが、近傍探索の応答（`GraphEdgeDto`）にも
    // 型カタログ（`EdgeTypeCatalogItemDto`）にも重みの項目が無い。**公開は GraphService 側の
    // 変更**であり、本 issue（#970）の作業範囲外である。
    //
    // **したがって、公開されるまでは全辺を既定重み（0.5 = `EdgeType.DefaultWeight`）で扱う。**
    // 再ランク側（`GraphProximity` / `GraphRerank`）は重みを一級の入力として実装してあるので、
    // 公開されればここが変わるだけで効き始める。
    //
    // 🔴 **黙って無差別にしない。** 重みが取れないあいだは要求ごとに警告を出す ——
    // 「型ごとの重み付けが効いている」と読める状態を作らないためである。
    internal const double UnavailableEdgeWeight = 0.5;

    public async Task<GraphNeighborhood> ExpandAsync(
        IReadOnlyList<Guid> seedDocumentIds, int hops, CancellationToken ct = default)
    {
        if (seedDocumentIds.Count == 0)
            return GraphNeighborhood.Empty;

        // 🔴 **資格情報が無ければ GraphService を呼ばない。**
        // 呼ぶと `GraphAccessResolver` が anonymous → `Granted=false` へ縮退し、**全部 404** になる。
        // それは「グラフには何も無い」と読める形の静かな故障である（#916a 仕様書 §繋ぎ方の帰結）。
        // **呼ばずに警告する** ——「効いていない」ことを運用が読める唯一の手掛かりである。
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorization))
        {
            logger.LogWarning(
                "Graph expansion skipped: the incoming request carries no Authorization header to "
                + "propagate to GraphService (a graph call without credentials degrades to 404 for "
                + "every hop, which is indistinguishable from an empty graph)");
            return GraphNeighborhood.Empty;
        }

        var client = httpFactory.CreateClient(ClientName);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);

        var views = await Task.WhenAll(seedDocumentIds.Select(id => FetchAsync(client, id, hops, ct)));

        // 辺は識別子で重複排除する（起点が複数あると同じ辺を複数回持ち帰る）。
        var edges = new Dictionary<Guid, GraphNeighborEdge>();
        foreach (var edge in views.SelectMany(v => v))
            edges.TryAdd(edge.Id, new GraphNeighborEdge(
                edge.SourceDocumentId, edge.TargetDocumentId, UnavailableEdgeWeight));

        if (edges.Count > 0)
            logger.LogWarning(
                "Graph re-ranking is running without edge-type weights: GraphService exposes no "
                + "weight on its graph or edge-type endpoints, so all {Count} edges are treated as "
                + "{Weight}. Type-based weighting (ADR-0035) is inert until the weight is published",
                edges.Count, UnavailableEdgeWeight);

        return new GraphNeighborhood([.. edges.Values]);
    }

    // 1 起点ぶんの近傍を取る。**失敗は検索そのものを落とさない**（段は補助であり、
    // 落とすと「グラフが不調なら検索が死ぬ」ことになる）。
    private async Task<IReadOnlyList<GraphEdgePayload>> FetchAsync(
        HttpClient client, Guid documentId, int hops, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetAsync($"/graph/{documentId}/neighbors?hops={hops}", ct);

            // 404 は「見えない・存在しない」の両方を意味する（ADR-0034 決定 2 の存在秘匿）。
            // **起点が見えないことは異常ではない**ので警告にしない。
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return [];

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Graph neighbors request for {DocumentId} failed with {Status}", documentId, resp.StatusCode);
                return [];
            }

            var view = await resp.Content.ReadFromJsonAsync<GraphViewPayload>(ct);

            // 🔴 **候補の入口は `edges` だけである。`nodes` は読まない。**
            // 未承認（pending / rejected）の AI 提案は辺として存在しない（#914 の状態機械）ので、
            // 辺だけを入口にすれば**構造的に**根拠へ混ざり得ない。
            return view?.Edges ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GraphService is unreachable; continuing without graph expansion");
            return [];
        }
    }

    // GraphService の応答のうち**本段が使う項目だけ**を写す（ノード・総数・打ち切りは使わない）。
    private sealed record GraphViewPayload(List<GraphEdgePayload>? Edges);

    private sealed record GraphEdgePayload(Guid Id, Guid SourceDocumentId, Guid TargetDocumentId);
}
