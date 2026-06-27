using KnowledgePlatform.Shared.Contracts.Dtos;
using RetrievalService.Api.Abstractions;

namespace RetrievalService.Api.Infrastructure;

// テスト用インメモリ実装（ADR-0009 ポート）
public class InMemoryVectorStore : IVectorStore
{
    private readonly List<ChunkPayload> _store = [];

    public Task<List<SearchResultDto>> SearchAsync(float[] queryVector, int topK,
        Dictionary<string, string>? attributeFilters, CancellationToken ct = default)
    {
        var filtered = _store.AsEnumerable();
        if (attributeFilters is { Count: > 0 })
            filtered = filtered.Where(c =>
                attributeFilters.All(kv =>
                    c.Attributes.TryGetValue(kv.Key, out var v) && v == kv.Value));

        var results = filtered
            .Take(topK)
            .Select(c => new SearchResultDto(c.ChunkId, c.DocumentId, c.DocumentTitle,
                c.Text, 0.9f, c.MarkdownUri, c.Attributes, c.Tags))
            .ToList();

        return Task.FromResult(results);
    }

    public Task UpsertAsync(ChunkPayload chunk, CancellationToken ct = default)
    {
        _store.RemoveAll(c => c.ChunkId == chunk.ChunkId);
        _store.Add(chunk);
        return Task.CompletedTask;
    }

    public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        _store.RemoveAll(c => c.DocumentId == documentId);
        return Task.CompletedTask;
    }
}
