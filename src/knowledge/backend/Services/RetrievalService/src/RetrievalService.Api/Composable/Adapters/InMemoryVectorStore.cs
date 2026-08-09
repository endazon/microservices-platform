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

    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（テスト/ローカル用の等価実装）。
    // **Qdrant 実装と同じ意味論**にする——同じ ABAC フィルタで絞った集合から、値集合だけを返す
    // （件数は返さない。ADR-0043 決定 2 / [[IADR-0151]] 決定 2）。
    public Task<List<string>> ListAttributeValuesAsync(
        string payloadKey, IReadOnlyList<AttributeFilter>? filters, CancellationToken ct = default)
    {
        var reachable = _store.Where(c => MatchesFilters(c, filters));

        // `tags` はリスト項目、それ以外は `attributes.<key>` のネスト項目（IADR-0014）。
        var values = payloadKey == AttributeValueKeys.Tags
            ? reachable.SelectMany(c => c.Tags)
            : reachable
                .Select(c => c.Attributes.GetValueOrDefault(
                    payloadKey.StartsWith($"{AttributeValueKeys.AttributesPrefix}.", StringComparison.Ordinal)
                        ? payloadKey[(AttributeValueKeys.AttributesPrefix.Length + 1)..]
                        : payloadKey))
                .OfType<string>();

        return Task.FromResult<List<string>>([.. values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)]);
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
