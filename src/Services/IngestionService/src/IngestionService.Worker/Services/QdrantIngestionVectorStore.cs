using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Worker.Services;

// ADR-0009: IngestionService から Qdrant へ直接書き込む
public class QdrantIngestionVectorStore(QdrantClient client, IConfiguration config)
    : IIngestionVectorStore
{
    // FR-02: コレクション名は CollectionName を正とし、後方互換で Collection、
    // 既定 knowledge_chunks の順に解決する（appsettings と RetrievalService に整合）。
    private readonly string _collection =
        config["Qdrant:CollectionName"] ?? config["Qdrant:Collection"] ?? "knowledge_chunks";

    private readonly ulong _vectorSize =
        ulong.TryParse(config["Qdrant:VectorSize"], out var v) ? v : 1536UL;

    // FR-02: 索引（コレクション）の存在を保証する。未作成なら作成する。
    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        if (await client.CollectionExistsAsync(_collection, ct))
            return;

        await client.CreateCollectionAsync(_collection,
            new VectorParams { Size = _vectorSize, Distance = Distance.Cosine },
            cancellationToken: ct);
    }

    public async Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, text, chunkIndex, markdownUri, attributes, tags);

        await client.UpsertAsync(_collection,
            [new PointStruct { Id = new PointId { Uuid = chunkId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
    }

    // FR-02, FR-05: チャンクの Qdrant ペイロードを構築する。
    // IADR-0014（選択肢C・実機検証済み・Issue #71）: ABAC 属性はネスト構造体 `attributes -> { k: v }`
    // へ統一する。RetrievalService.QdrantVectorStore の書き込み・フィルタ表現と一致させる。
    internal static Dictionary<string, Value> BuildChunkPayload(Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags)
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

    public async Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        await client.DeleteAsync(_collection,
            new Filter
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
            }, cancellationToken: ct);
    }
}
