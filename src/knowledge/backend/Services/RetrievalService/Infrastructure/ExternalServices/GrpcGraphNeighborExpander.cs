using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using Pb = Knowledge.Contracts.Grpc.Graph.V1;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-04, FR-05, FR-17, NFR-09, NFR-16, UC-10, SC-18, ADR-0004, ADR-0029, ADR-0034 決定 1・2,
// ADR-0035 決定 2, ADR-0075, 計画 ADR-0086 決定 1・3・5, [[IADR-0242]], [[IADR-0379]] 決定 4・5,
// [[IADR-0397]], [[IADR-0401]], [[IADR-0402]], [[IADR-0408]], [[IADR-0410]] (#1255):
// 近傍展開ポートの **gRPC 版**。
//
// **並走中の正は REST である。** 本実装は `Services:GraphServiceGrpc` が構成されたときだけ
// 登録され（`AddGraphNeighborsGrpcClient`）、無ければ `GraphServiceNeighborExpander` のままである。
// 戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **権限伝播は「利用者文脈を本文で運ぶ」へ変わった**（計画 `ADR-0086` 決定 1）。
// 従前は利用者の `Authorization` ヘッダを転送していた（方式 A）。メタデータに載るのは
// **本サービス自身の s2s トークン**だけであり、利用者のトークンは面を通らない
// （通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy が成立する）。
//
// 🔴 **判定の位置は動かない**（`ADR-0034` 決定 1 のホップごと ABAC）。GraphService は
// 受け取った利用者文脈で**自分の**スコープを解決してから探索する。
// **解決済み scope を本文で渡す方式 B は採らない** —— 採ると GraphService に
// 「本文で渡された scope を信じる」口が開き、そこへ到達できる誰もが任意の scope を主張できる
// （`ADR-0086` の 2026-09-07 追記が同じ理由でその案を退けた）。
//
// 🔴 **縮退の枝を 1 つも増やさず・1 つも減らさない。** REST 版との対応は次のとおりである。
//
// | 事象 | REST | ここ |
// | --- | --- | --- |
// | 資格情報が無い | 呼ばずに警告 → 空 | 同じ（`IsAuthenticated` を見る） |
// | 起点が見えない・無い | 404 → `[]`（**警告しない**） | `found=false` → `[]`（同じ） |
// | 近傍の取得が失敗・不達 | 非 2xx / 例外 → 警告 → `[]` | `RpcException` ほか → 警告 → `[]` |
// | 辞書が引けない | 非 2xx / 例外 → 警告 → 全辺フォールバック重み | 同じ |
// | 辞書に無い型 | 警告 → フォールバック重み | 同じ |
//
// 🔴 **段の失敗は検索そのものを落とさない**（REST 版と同じ姿勢）—— 落とすと
// 「グラフが不調なら検索が死ぬ」ことになる。**ただし静かに無差別へ落ちない**（必ず警告する）。
public sealed class GrpcGraphNeighborExpander(
    Pb.GraphNeighbors.GraphNeighborsClient client,
    ILogger<GrpcGraphNeighborExpander> logger)
    : IGraphNeighborExpander
{
    public async Task<GraphNeighborhood> ExpandAsync(
        IReadOnlyList<Guid> seedDocumentIds, int hops, SearchUserContext user,
        CancellationToken ct = default)
    {
        if (seedDocumentIds.Count == 0)
            return GraphNeighborhood.Empty;

        // 🔴 **利用者が分からなければ GraphService を呼ばない。**
        // 呼ぶと呼び出し先が `INVALID_ARGUMENT` を返し、それは「引けなかった」枝へ落ちて
        // **全部空**になる。それは「グラフには何も無い」と読める形の静かな故障である
        // （REST 版が資格情報の不在で同じ判断をしているのと**同じ値・同じ副作用**）。
        // **呼ばずに警告する** ——「効いていない」ことを運用が読める唯一の手掛かりである。
        //
        // 🔴 **利用者は引数で受け取る**（[[IADR-0426]] 決定 2）。従前は `IHttpContextAccessor`
        // から拾っていたが、east-west gRPC の入口では器に居るのは**呼び出し元サービスの
        // s2s 主体**であり、拾うと `service-account-…` が ABAC の主体として本文に載る ——
        // 例外は 1 つも起きず、グラフ展開だけが静かに空になる。
        if (!user.IsAuthenticated || string.IsNullOrWhiteSpace(user.UserId))
        {
            logger.LogWarning(
                "Graph expansion skipped: this search carries no authenticated user to "
                + "pass to GraphService as request-body context (a graph call without a user "
                + "degrades to an empty result for every hop, which is indistinguishable from an "
                + "empty graph)");
            return GraphNeighborhood.Empty;
        }

        var userContext = ToUserContext(user);

        // 辺の型の重み。**1 要求につき 1 回**引く（REST 版と同じ。キャッシュは持たない）。
        var weights = await FetchWeightsAsync(ct);

        var views = await Task.WhenAll(
            seedDocumentIds.Select(id => FetchAsync(id, hops, userContext, ct)));

        // 辺は識別子で重複排除する（起点が複数あると同じ辺を複数回持ち帰る）。
        var edges = new Dictionary<Guid, GraphNeighborEdge>();
        var unknownTypes = new HashSet<Guid>();
        foreach (var edge in views.SelectMany(v => v))
        {
            if (!Guid.TryParse(edge.Id, out var id)
                || !Guid.TryParse(edge.SourceDocumentId, out var source)
                || !Guid.TryParse(edge.TargetDocumentId, out var target)
                || !Guid.TryParse(edge.EdgeTypeId, out var edgeTypeId))
                continue;

            var weight = GraphServiceNeighborExpander.FallbackEdgeWeight;
            if (weights is not null && !weights.TryGetValue(edgeTypeId, out weight))
            {
                unknownTypes.Add(edgeTypeId);
                weight = GraphServiceNeighborExpander.FallbackEdgeWeight;
            }

            edges.TryAdd(id, new GraphNeighborEdge(source, target, weight));
        }

        if (unknownTypes.Count > 0)
            logger.LogWarning(
                "Graph re-ranking met {Count} edge type(s) missing from the edge-type catalog; "
                + "their edges fall back to weight {Weight}",
                unknownTypes.Count, GraphServiceNeighborExpander.FallbackEdgeWeight);

        return new GraphNeighborhood([.. edges.Values]);
    }

    // 辞書（型識別子 → 重み）を取る。**失敗は検索そのものを落とさない** —— 全辺フォールバック
    // 重みの縮退（null）へ倒し、警告を出す（REST 版と同じ値・同じ副作用）。
    private async Task<IReadOnlyDictionary<Guid, double>?> FetchWeightsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await client.ListEdgeTypeWeightsAsync(
                new Pb.ListEdgeTypeWeightsRequest(), cancellationToken: ct);

            var map = new Dictionary<Guid, double>();
            foreach (var item in resp.Weights)
                if (Guid.TryParse(item.EdgeTypeId, out var id))
                    map[id] = item.Weight;
            return map;
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            logger.LogWarning(ex,
                "Edge-type catalog is unreachable; re-ranking falls back to weight {Weight} for "
                + "every edge", GraphServiceNeighborExpander.FallbackEdgeWeight);
            return null;
        }
    }

    // 1 起点ぶんの近傍を取る。**失敗は検索そのものを落とさない**。
    private async Task<IReadOnlyList<Pb.NeighborEdge>> FetchAsync(
        Guid documentId, int hops, Pb.UserContext user, CancellationToken ct)
    {
        try
        {
            var resp = await client.ExpandNeighborsAsync(
                new Pb.ExpandNeighborsRequest
                {
                    DocumentId = documentId.ToString(),
                    Hops = hops,
                    User = user,
                },
                cancellationToken: ct);

            // 🔴 **`found=false` は警告しない。** 「見えない・存在しない」の両方を意味し
            // （`ADR-0034` 決定 2 の存在秘匿）、**起点が見えないことは異常ではない**
            // （REST 版が 404 を警告しないのと同じ）。
            return resp.Found ? resp.Edges : [];
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            // REST の「非 2xx」と「到達できない」は gRPC では同じ例外に畳まれる。
            // s2s トークンの取得失敗（`InvalidOperationException`）もここへ落ちる。
            logger.LogWarning(ex,
                "Graph neighbors request for {DocumentId} failed; continuing without graph expansion",
                documentId);
            return [];
        }
    }

    // 🔴 **載せるのは判定の入力であって判定結果ではない**（計画 `ADR-0086` 決定 1）。
    // FR-05, ADR-0080, IADR-0411 (#1323): 属性の抽出はプラットフォーム唯一の点
    // （`BffScopeResolver.ExtractUserAttributes`）で既に済んでいる —— `SearchUserContext` が
    // それを運ぶ。**ここでキーを列挙しない。**
    internal static Pb.UserContext ToUserContext(SearchUserContext user)
    {
        var context = new Pb.UserContext
        {
            UserId = user.UserId,
            // 🔴 **`action` は既定へ丸めない。** 読み取り経路も `read` を明示して送る
            // （[[IADR-0272]] 決定 4 / [[IADR-0401]] と同じ作法）。
            Action = SearchUserContext.ReadAction,
        };
        foreach (var (key, value) in user.Attributes)
            context.UserAttributes[key] = value;
        return context;
    }

    // シャットダウンは呼び出し元の事情であり、展開の失敗ではない。
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;
}

// FR-04, FR-17, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0410]] (#1255):
// GraphService 宛の生成クライアントの登録。参照実装 `AddAuthzScopeGrpcClient` と同型。
public static class GraphNeighborsGrpcClientExtensions
{
    /// <summary>`Services:GraphServiceGrpc`（h2c のアドレス。例: http://graph-service:8081）。</summary>
    public const string AddressKey = "Services:GraphServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "GraphServiceGrpc";

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddGraphNeighborsGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。本サービスは既に LlmGateway 宛のチャネルを持つ ——
        // 2 つ目をキー無しで足すと、近傍展開が**ゲートウェイへ繋がる**（あるいはその逆）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.GraphNeighbors.GraphNeighborsClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
