using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Worker.Services;

// ADR-0009: IngestionService から Qdrant へ直接書き込む
public class QdrantIngestionVectorStore(QdrantClient client, IConfiguration config)
    : IIngestionVectorStore
{
    private readonly string _collection = config["Qdrant:Collection"] ?? "knowledge-chunks";

    public async Task UpsertChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new Value { StringValue = documentId.ToString() },
            ["document_title"] = new Value { StringValue = title },
            ["text"] = new Value { StringValue = text },
            ["markdown_uri"] = new Value { StringValue = markdownUri ?? "" },
        };
        foreach (var (k, v) in attributes)
            payload[$"attributes.{k}"] = new Value { StringValue = v };

        await client.UpsertAsync(_collection,
            [new PointStruct { Id = new PointId { Uuid = chunkId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
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
