using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Infrastructure.ExternalServices;

// ADR-0009, ADR-0016: IngestionService から Qdrant へ直接書き込む（モデル別コレクション対応）。
public class QdrantIngestionVectorStore(
    QdrantClient client, IOptions<EmbeddingCollectionsOptions> collections)
    : IIngestionVectorStore
{
    private readonly IReadOnlyList<EmbeddingCollectionOptions> _collections = collections.Value.Collections;

    // FR-02: 全モデル別コレクションの存在を保証する。未作成なら各コレクションの次元で作成する。
    public async Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        foreach (var c in _collections)
        {
            if (await client.CollectionExistsAsync(c.Name, ct))
                continue;

            await client.CreateCollectionAsync(c.Name,
                new VectorParams { Size = (ulong)c.VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    public async Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, text, chunkIndex, markdownUri, attributes, tags, updatedAt);

        await client.UpsertAsync(collection,
            [new PointStruct { Id = new PointId { Uuid = chunkId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
    }

    // FR-02, FR-05: チャンクの Qdrant ペイロードを構築する。
    // IADR-0014（選択肢C・実機検証済み・Issue #71）: ABAC 属性はネスト構造体 `attributes -> { k: v }`
    // へ統一する。RetrievalService.QdrantVectorStore の書き込み・フィルタ表現と一致させる。
    internal static Dictionary<string, Value> BuildChunkPayload(Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null)
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new Value { StringValue = documentId.ToString() },
            ["document_title"] = new Value { StringValue = title },
            ["text"] = new Value { StringValue = text },
            ["markdown_uri"] = new Value { StringValue = markdownUri ?? "" },
            // FR-02: チャンクの並び順・出典の一部として保持
            ["chunk_index"] = new Value { IntegerValue = chunkIndex },
        };

        // FR-03, SC-02, #536: 文書の更新日時（利用者裁定 Q6）。**Unix epoch ミリ秒の整数**で持つ
        // （IADR-0149 決定 1）。ISO-8601 文字列は同じ時刻を `+09:00` とも `Z` とも書けるため、
        // 文字列のまま並べると辞書順が実時刻順と一致しない（並び順は #532 が使う）。
        // 検索側（RetrievalService.QdrantVectorStore）の書き込み・復元と表現を揃えること。
        if (updatedAt is { } at)
            payload["updated_at"] = new Value { IntegerValue = at.ToUnixTimeMilliseconds() };

        // FR-02: タグをペイロードに保持（検索結果の絞り込み・表示用）
        if (tags.Count > 0)
        {
            var tagList = new ListValue();
            foreach (var t in tags)
                tagList.Values.Add(new Value { StringValue = t });
            payload["tags"] = new Value { ListValue = tagList };
        }

        // FR-05: ABAC 属性をペイロードに保持（検索時フィルタ用）。ネスト構造体へ統一する（IADR-0014 選択肢C）。
        if (attributes.Count > 0)
        {
            var attrs = new Struct();
            foreach (var (k, val) in attributes)
                attrs.Fields[k] = new Value { StringValue = val };
            payload["attributes"] = new Value { StructValue = attrs };
        }

        return payload;
    }

    // FR-02, FR-05: 全モデル別コレクションから当該文書のチャンクを削除する（機密区分変更時の残存防止）。
    public async Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document_id",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };

        foreach (var c in _collections)
            await client.DeleteAsync(c.Name, filter, cancellationToken: ct);
    }
}
