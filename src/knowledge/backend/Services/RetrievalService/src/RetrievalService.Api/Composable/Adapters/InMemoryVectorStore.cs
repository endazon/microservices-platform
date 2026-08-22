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

    // FR-04, FR-17, ADR-0035, #969: 文書 ID 集合に絞った意味検索（二段検索の後段）。
    // **Qdrant 実装と同じ意味論**にする——文書 ID の制約は ABAC を置き換えず **AND** で重なり、
    // 🔴 **空集合は「該当なし」であって「全件」ではない**（グラフが 0 件を返したときに全文書へ広げない）。
    //
    // 🔴 **本口は `queryVector` を実際に見る**（コサイン類似度で採点し降順に並べる）。
    // 上の `SearchAsync` は `queryVector` を参照せずスコア `0.9f` を返す作りであり、
    // **ベクトル側の欠陥がテストで緑のまま通り抜ける**（#995 で実際に起きた）。
    // 二段検索の後段はチャンクの `Score` そのものを出典へ載せるため、
    // ここで「見ているふり」をすると #970 の順位検証が空振りする。
    // 空ベクトル・零ベクトル・次元不一致はスコア 0 とする（#995 の縮退で空ベクトルが渡り得るため、
    // 例外にしない）。
    public Task<List<SearchResultDto>> SearchWithinDocumentsAsync(float[] queryVector, int topK,
        IReadOnlyCollection<Guid> documentIds, IReadOnlyList<AttributeFilter>? filters,
        CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return Task.FromResult(new List<SearchResultDto>());

        var scope = documentIds.ToHashSet();

        var results = _store
            .Where(c => MatchesFilters(c, filters))
            .Select(c => (Chunk: c, Score: CosineSimilarity(queryVector, c.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new SearchResultDto(x.Chunk.ChunkId, x.Chunk.DocumentId, x.Chunk.DocumentTitle,
                x.Chunk.Text, x.Score, x.Chunk.MarkdownUri, x.Chunk.Attributes, x.Chunk.Tags,
                x.Chunk.UpdatedAt))
            .ToList();

        return Task.FromResult(results);
    }

    // FR-04, #969: コサイン類似度。次元が違う／ノルムが 0 のときは 0（比較不能を「似ていない」とする）。
    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return 0f;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0f : (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
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
    // FR-05: フィルタ間は AND、値集合内は OR（Qdrant 側と同じ意味論）。
    //
    // **［#539］`tags` を絞れるようにした。** 従前は `c.Attributes` しか見ておらず、
    // **`tags` を指定すると必ず 0 件になっていた**（属性辞書に `tags` というキーが無いため）。
    // **写像の判定は `AttributeValueKeys.ToPayloadKey` に寄せて Qdrant 側と 1 つの真実にする**——
    // ここで独自に `f.Key == "tags"` と書くと、片方だけ直したとき静かに割れる。
    //
    // **タグはリストなので「いずれかが一致」で真になる**（属性は単一値の完全一致）。
    private static bool MatchesFilters(ChunkPayload c, IReadOnlyList<AttributeFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return true;

        return filters.All(f =>
            AttributeValueKeys.ToPayloadKey(f.Key) == AttributeValueKeys.Tags
                ? c.Tags.Any(t => f.AllowedValues.Contains(t, StringComparer.OrdinalIgnoreCase))
                : c.Attributes.TryGetValue(f.Key, out var v)
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
