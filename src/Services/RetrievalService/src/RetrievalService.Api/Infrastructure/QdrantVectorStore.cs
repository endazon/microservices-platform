using KnowledgePlatform.Shared.Contracts.Dtos;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Api.Abstractions;

namespace RetrievalService.Api.Infrastructure;

// ADR-0009: Qdrant 実装（ポート = IVectorStore）
public class QdrantVectorStore(QdrantClient client, IConfiguration config) : IVectorStore
{
    private readonly string _collection = config["Qdrant:Collection"] ?? "knowledge-chunks";

    public async Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK,
        Dictionary<string, string>? attributeFilters,
        CancellationToken ct = default)
    {
        // FR-05: ABAC 属性フィルタをQdrantのペイロードフィルタに変換
        Filter? filter = null;
        if (attributeFilters is { Count: > 0 })
        {
            var conditions = attributeFilters.Select(kv =>
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = $"attributes.{kv.Key}",
                        Match = new Match { Keyword = kv.Value }
                    }
                }).ToList();
            filter = new Filter { Must = { conditions } };
        }

        var results = await client.SearchAsync(_collection, queryVector, limit: (ulong)topK,
            filter: filter, cancellationToken: ct);

        return results.Select(r => new SearchResultDto(
            ChunkId: Guid.Parse(r.Id.Uuid),
            DocumentId: Guid.TryParse(r.Payload.GetValueOrDefault("document_id")?.StringValue, out var docId) ? docId : Guid.Empty,
            DocumentTitle: r.Payload.GetValueOrDefault("document_title")?.StringValue ?? "",
            Text: r.Payload.GetValueOrDefault("text")?.StringValue ?? "",
            Score: r.Score,
            MarkdownUri: r.Payload.GetValueOrDefault("markdown_uri")?.StringValue,
            Attributes: [],
            Tags: []
        )).ToList();
    }

    public async Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new Value { StringValue = chunk.DocumentId.ToString() },
            ["document_title"] = new Value { StringValue = chunk.DocumentTitle },
            ["text"] = new Value { StringValue = chunk.Text },
            ["markdown_uri"] = new Value { StringValue = chunk.MarkdownUri ?? "" },
        };
        // FR-05: ABAC 属性をペイロードに保持（検索時フィルタ用）
        foreach (var (k, v) in chunk.Attributes)
            payload[$"attributes.{k}"] = new Value { StringValue = v };

        var point = new PointStruct
        {
            Id = new PointId { Uuid = chunk.ChunkId.ToString() },
            Vectors = chunk.Vector,
            Payload = { payload }
        };

        await client.UpsertAsync(_collection, [point], cancellationToken: ct);
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
