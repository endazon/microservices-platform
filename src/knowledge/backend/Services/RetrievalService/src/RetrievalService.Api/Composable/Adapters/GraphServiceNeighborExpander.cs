using Knowledge.Contracts.Dtos;
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

    // FR-04, ADR-0035 決定 2 (#970): 辺の型の重みは**辞書（`/graph/edge-types/catalog`）の実値**を
    // 使う（`edge_types.Weight` の公開面。#947a が値を、#970 が公開と消費を入れた）。
    //
    // 🔴 **フォールバックは中庸（0.5 = GraphService の `EdgeType.DefaultWeight` と同値）。**
    // 使うのは 2 つの縮退だけである —— ①辞書が引けない（不達・非 2xx）②辺の型が辞書に無い
    // （探索と辞書取得の間に型が消された等）。**いずれも警告を出し、静かに無差別へ落ちない。**
    internal const double FallbackEdgeWeight = 0.5;

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

        // 辺の型の重み。**1 要求につき 1 回**、近傍探索と同じ資格情報で引く（キャッシュは持たない ——
        // 既存のカタログ消費者（BFF プロキシ）も都度取得であり、測らずに最適化しない）。
        var weights = await FetchWeightsAsync(client, ct);

        var views = await Task.WhenAll(seedDocumentIds.Select(id => FetchAsync(client, id, hops, ct)));

        // 辺は識別子で重複排除する（起点が複数あると同じ辺を複数回持ち帰る）。
        var edges = new Dictionary<Guid, GraphNeighborEdge>();
        var unknownTypes = new HashSet<Guid>();
        foreach (var edge in views.SelectMany(v => v))
        {
            var weight = FallbackEdgeWeight;
            if (weights is not null && !weights.TryGetValue(edge.EdgeTypeId, out weight))
            {
                unknownTypes.Add(edge.EdgeTypeId);
                weight = FallbackEdgeWeight;
            }

            edges.TryAdd(edge.Id, new GraphNeighborEdge(
                edge.SourceDocumentId, edge.TargetDocumentId, weight));
        }

        if (unknownTypes.Count > 0)
            logger.LogWarning(
                "Graph re-ranking met {Count} edge type(s) missing from the edge-type catalog; "
                + "their edges fall back to weight {Weight}", unknownTypes.Count, FallbackEdgeWeight);

        return new GraphNeighborhood([.. edges.Values]);
    }

    // 辞書（型識別子 → 重み）を取る。**失敗は検索そのものを落とさない** —— 全辺フォールバック
    // 重みの縮退（null）へ倒し、警告を出す（型ごとの重み付けが効いていないことを運用から読める形）。
    private async Task<IReadOnlyDictionary<Guid, double>?> FetchWeightsAsync(
        HttpClient client, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetAsync("/graph/edge-types/catalog", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Edge-type catalog request failed with {Status}; re-ranking falls back to "
                    + "weight {Weight} for every edge", resp.StatusCode, FallbackEdgeWeight);
                return null;
            }

            var items = await resp.Content.ReadFromJsonAsync<List<EdgeTypeCatalogItemDto>>(ct);
            return (items ?? []).ToDictionary(i => i.Id, i => i.Weight);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Edge-type catalog is unreachable; re-ranking falls back to weight {Weight} for "
                + "every edge", FallbackEdgeWeight);
            return null;
        }
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

    // `EdgeTypeId` は辞書（型識別子 → 重み）の突合キーである。重みを辺へ複写しないのは
    // GraphService 側と同じ理由（真実源は `edge_types`。改名・編集に自動追随させる）。
    private sealed record GraphEdgePayload(
        Guid Id, Guid SourceDocumentId, Guid TargetDocumentId, Guid EdgeTypeId);
}
