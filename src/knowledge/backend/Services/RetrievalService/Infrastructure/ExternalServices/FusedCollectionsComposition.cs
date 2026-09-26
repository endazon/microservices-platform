using Qdrant.Client;
using RetrievalService.Common.Observability;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, ADR-0092 決定 1・2, [[IADR-0467]] (#336): 束ねる追加コレクションの**組み立て**（合成点から呼ぶ）。
//
// 1 コレクション = 「そのコレクションを読む Qdrant 実装」＋「そのコレクションを名乗って埋める客体」。
// 埋め込みの輸送は主と同じ選び方に従う（`Services:LlmGatewayGrpc` が在れば gRPC、無ければ REST）——
// **主と追加で輸送が割れると、片方の経路だけで照合や資格情報の付け方が違う状態が作れてしまう。**
//
// 🔴 **REST は主と同じ名前つきクライアント**（`LlmGatewayEmbeddingService.HttpClientName`）から作る。
// 宛先と s2s トークンの付け方（`AddLlmGatewayServiceToken`）を 2 か所に書かないためである。
// **要求ごとに作る**（Scoped）—— 型つきクライアントと同じく、`HttpClient` を単一インスタンスに
// 抱え込まない（ハンドラの入れ替えを殺さない）。
internal static class FusedCollectionsComposition
{
    internal static FusedCollections Build(
        IServiceProvider sp, IReadOnlyList<string> names, bool useGrpc)
    {
        if (names.Count == 0)
            return FusedCollections.None;

        var client = sp.GetRequiredService<QdrantClient>();
        var storeLogger = sp.GetRequiredService<ILogger<QdrantVectorStore>>();
        var metrics = sp.GetRequiredService<KeywordSearchMetrics>();

        var items = new List<FusedCollection>(names.Count);
        foreach (var name in names)
        {
            var target = new QueryEmbeddingTarget(name, NamedInRequest: true);
            IEmbeddingService embed = useGrpc
                ? new LlmGatewayGrpcEmbeddingService(
                    sp.GetRequiredService<Pb.LlmEmbedding.LlmEmbeddingClient>(), target,
                    sp.GetService<ILogger<LlmGatewayGrpcEmbeddingService>>())
                : new LlmGatewayEmbeddingService(
                    sp.GetRequiredService<IHttpClientFactory>()
                        .CreateClient(LlmGatewayEmbeddingService.HttpClientName),
                    target,
                    sp.GetService<ILogger<LlmGatewayEmbeddingService>>());

            items.Add(new FusedCollection(
                name, QdrantVectorStore.ForCollection(client, name, storeLogger, metrics), embed));
        }

        return new FusedCollections(items);
    }
}
