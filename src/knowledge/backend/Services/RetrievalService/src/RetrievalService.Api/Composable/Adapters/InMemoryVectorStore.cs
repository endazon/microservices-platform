using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Api.Foundation.Ports;

namespace RetrievalService.Api.Composable.Adapters;

// テスト用インメモリ実装（ADR-0009 ポート）
public class InMemoryVectorStore : IVectorStore
{
    private readonly List<ChunkPayload> _store = [];

    public Task<List<SearchResultDto>> SearchAsync(float[] queryVector, int topK,
        IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
    {
        var results = _store
            .Where(c => MatchesFilters(c, filters))
            .Take(topK)
            .Select(c => new SearchResultDto(c.ChunkId, c.DocumentId, c.DocumentTitle,
                c.Text, 0.9f, c.MarkdownUri, c.Attributes, c.Tags, c.UpdatedAt))
            .ToList();

        return Task.FromResult(results);
    }

    // FR-03: 全文検索（語句オーバーラップによる簡易キーワード一致。テスト/ローカル用）
    public Task<List<SearchResultDto>> KeywordSearchAsync(string query, int topK,
        IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(new List<SearchResultDto>());

        var terms = Tokenize(query);

        var results = _store
            .Where(c => MatchesFilters(c, filters))
            .Select(c => (Chunk: c, Hits: terms.Count(t => c.Text.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Take(topK)
            .Select(x => new SearchResultDto(x.Chunk.ChunkId, x.Chunk.DocumentId, x.Chunk.DocumentTitle,
                x.Chunk.Text, x.Hits, x.Chunk.MarkdownUri, x.Chunk.Attributes, x.Chunk.Tags, x.Chunk.UpdatedAt))
            .ToList();

        return Task.FromResult(results);
    }

    // FR-05: ABAC 多値 allow-list 評価。フィルタ間は AND、値集合内は OR。
    // 属性キーを持たない文書は不一致（deny-by-default）。
    private static bool MatchesFilters(ChunkPayload c, IReadOnlyList<AttributeFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return true;

        return filters.All(f =>
            c.Attributes.TryGetValue(f.Key, out var v)
            && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
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
