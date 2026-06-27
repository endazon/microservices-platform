using KnowledgePlatform.Shared.Contracts.Dtos;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Api.Abstractions;

namespace RetrievalService.Api.Infrastructure;

// ADR-0009: Qdrant 実装（ポート = IVectorStore）
public class QdrantVectorStore(
    QdrantClient client, IConfiguration config, ILogger<QdrantVectorStore> logger)
    : IVectorStore
{
    // FR-02 整合: 取り込み（IngestionService）と同一のコレクション名解決にする。
    // CollectionName を正とし、後方互換で Collection、既定 knowledge_chunks の順。
    private readonly string _collection =
        config["Qdrant:CollectionName"] ?? config["Qdrant:Collection"] ?? "knowledge_chunks";

    public async Task<List<SearchResultDto>> SearchAsync(
        float[] queryVector, int topK,
        IReadOnlyList<AttributeFilter>? filters,
        CancellationToken ct = default)
    {
        var filter = BuildAttributeFilter(filters);

        var results = await client.SearchAsync(_collection, queryVector, limit: (ulong)topK,
            filter: filter, cancellationToken: ct);

        return results.Select(r => MapPayload(r.Id.Uuid, r.Payload, r.Score)).ToList();
    }

    // FR-03: 全文検索（Qdrant のペイロード `text` への full-text Match）
    public async Task<List<SearchResultDto>> KeywordSearchAsync(
        string query, int topK,
        IReadOnlyList<AttributeFilter>? filters,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var conditions = new List<Condition>
        {
            new() { Field = new FieldCondition { Key = "text", Match = new Match { Text = query } } }
        };
        // FR-05: ABAC 属性フィルタを全文検索にも適用（権限外文書を候補から除外）
        conditions.AddRange(BuildAttributeConditions(filters));

        try
        {
            var points = await client.ScrollAsync(_collection,
                filter: new Filter { Must = { conditions } },
                limit: (uint)topK, cancellationToken: ct);

            // Scroll は順序のみ（スコアなし）。順位を擬似スコア化し、融合は RRF が順位で行う。
            var rank = 0;
            return points.Result
                .Select(p => MapPayload(p.Id.Uuid, p.Payload, 1f / ++rank))
                .ToList();
        }
        catch (RpcException ex)
        {
            // 全文インデックス未作成等の場合はベクトルのみへ degrade（検索全体は失敗させない）
            logger.LogWarning(ex, "Keyword search unavailable; falling back to vector-only");
            return [];
        }
    }

    private static Filter? BuildAttributeFilter(IReadOnlyList<AttributeFilter>? filters)
    {
        // FR-05: ABAC 多値 allow-list を Qdrant のペイロードフィルタに変換
        var conditions = BuildAttributeConditions(filters);
        return conditions.Count > 0 ? new Filter { Must = { conditions } } : null;
    }

    // FR-05: 各属性キーを「key ∈ AllowedValues」（Match.Keywords=いずれか一致）へ変換。
    // キー間は呼び出し側の Must（AND）で結合される。
    private static List<Condition> BuildAttributeConditions(IReadOnlyList<AttributeFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return [];

        return filters
            .Where(f => f.AllowedValues.Count > 0)
            .Select(f => new Condition
            {
                Field = new FieldCondition
                {
                    Key = $"attributes.{f.Key}",
                    Match = new Match { Keywords = new RepeatedStrings { Strings = { f.AllowedValues } } }
                }
            })
            .ToList();
    }

    private static SearchResultDto MapPayload(
        string idUuid, IReadOnlyDictionary<string, Value> payload, float score) =>
        new(
            ChunkId: Guid.Parse(idUuid),
            DocumentId: Guid.TryParse(payload.GetValueOrDefault("document_id")?.StringValue, out var docId) ? docId : Guid.Empty,
            DocumentTitle: payload.GetValueOrDefault("document_title")?.StringValue ?? "",
            Text: payload.GetValueOrDefault("text")?.StringValue ?? "",
            Score: score,
            MarkdownUri: payload.GetValueOrDefault("markdown_uri")?.StringValue,
            Attributes: [],
            Tags: []);

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
