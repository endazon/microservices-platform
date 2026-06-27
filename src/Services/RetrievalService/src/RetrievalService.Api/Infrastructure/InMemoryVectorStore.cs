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

    // FR-03: 全文検索（語句オーバーラップによる簡易キーワード一致。テスト/ローカル用）
    public Task<List<SearchResultDto>> KeywordSearchAsync(string query, int topK,
        Dictionary<string, string>? attributeFilters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new List<SearchResultDto>());

        var terms = Tokenize(query);

        var filtered = _store.AsEnumerable();
        if (attributeFilters is { Count: > 0 })
            filtered = filtered.Where(c =>
                attributeFilters.All(kv =>
                    c.Attributes.TryGetValue(kv.Key, out var v) && v == kv.Value));

        var results = filtered
            .Select(c => (Chunk: c, Hits: terms.Count(t => c.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Take(topK)
            .Select(x => new SearchResultDto(x.Chunk.ChunkId, x.Chunk.DocumentId, x.Chunk.DocumentTitle,
                x.Chunk.Text, x.Hits, x.Chunk.MarkdownUri, x.Chunk.Attributes, x.Chunk.Tags))
            .ToList();

        return Task.FromResult(results);
    }

    private static string[] Tokenize(string query) =>
        query.Split([' ', '\t', '\n', '　', ',', '、'], StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries);

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
