using Qdrant.Client;
using RetrievalService.Common.Observability;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, ADR-0092 決定 1・2, [[IADR-0467]] (#336): 束ねる追加コレクションの**組み立て**（合成点から呼ぶ）。
//
// 1 コレクション = 「そのコレクションを読む Qdrant 実装」＋「そのコレクションを名乗って埋める客体」。
// 埋め込みの輸送は主と同じ east-west gRPC（`LlmEmbedding/Embed`）である（［2026-10-10 / #1255・[[IADR-0533]]］REST の並走は撤去した）。
// 生成クライアントは主と同じ 1 つ（宛先・s2s トークン）を使う —— 宛先と資格情報の付け方を 2 か所に書かない。
internal static class FusedCollectionsComposition
{
    //
    // FR-03, ADR-0127 決定 1・2, [[IADR-0497]] 決定 5 (#1746): `lexicalCollection` を渡すと、**語彙索引を最後に
    // 束ねる**（`LexicalOnly = true`・埋め込みの客体は `NoQueryEmbedding`）。読み方（ABAC・全文・復元）は
    // 主と 1 行も違わない `QdrantVectorStore` である。null は語彙索引を束ねない（試験・旧来の呼び出し）。
    internal static FusedCollections Build(
        IServiceProvider sp, IReadOnlyList<string> names, string? lexicalCollection = null)
    {
        if (names.Count == 0 && lexicalCollection is null)
            return FusedCollections.None;

        var client = sp.GetRequiredService<QdrantClient>();
        var storeLogger = sp.GetRequiredService<ILogger<QdrantVectorStore>>();
        var metrics = sp.GetRequiredService<KeywordSearchMetrics>();

        var items = new List<FusedCollection>(names.Count + 1);
        foreach (var name in names)
        {
            var target = new QueryEmbeddingTarget(name, NamedInRequest: true);
            IEmbeddingService embed = new LlmGatewayGrpcEmbeddingService(
                sp.GetRequiredService<Pb.LlmEmbedding.LlmEmbeddingClient>(), target,
                sp.GetService<ILogger<LlmGatewayGrpcEmbeddingService>>());

            items.Add(new FusedCollection(
                name, QdrantVectorStore.ForCollection(client, name, storeLogger, metrics), embed));
        }

        // 🔴 **語彙索引は最後に置く**（RRF の同点は初出順。ベクトルを持つコレクションの並びを先に保つ）。
        if (lexicalCollection is not null)
            items.Add(new FusedCollection(
                lexicalCollection,
                // #1746 監査 🟡3: 語彙索引は取り込みが作るので、無いうちの削除は no-op（IADR-0497 決定 4）。
                QdrantVectorStore.ForCollection(client, lexicalCollection, storeLogger, metrics,
                    missingCollectionIsEmpty: true),
                NoQueryEmbedding.Instance,
                LexicalOnly: true));

        return new FusedCollections(items);
    }
}
