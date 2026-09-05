using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Indexing;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Infrastructure.ExternalServices;

// テスト用インメモリ実装（ADR-0009 ポート）
public class InMemoryVectorStore : IVectorStore
{
    private readonly List<ChunkPayload> _store = [];

    public Task<List<SearchResultDto>> SearchAsync(float[] queryVector, int topK,
        ScopeFilter? filters, CancellationToken ct = default)
    {
        var results = _store
            .Where(c => MatchesFilters(c, filters))
            .Take(topK)
            .Select(c => ToResult(c, 0.9f))
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
        IReadOnlyCollection<Guid> documentIds, ScopeFilter? filters,
        CancellationToken ct = default)
    {
        if (documentIds.Count == 0)
            return Task.FromResult(new List<SearchResultDto>());

        var scope = documentIds.ToHashSet();

        var results = _store
            .Where(c => scope.Contains(c.DocumentId) && MatchesFilters(c, filters))
            .Select(c => (Chunk: c, Score: CosineSimilarity(queryVector, c.Vector)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => ToResult(x.Chunk, x.Score))
            .ToList();

        return Task.FromResult(results);
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 3:
    // **Qdrant 実装と同じ射影を通す唯一の点。**
    //
    // 🔴 `ChunkPayload.Text` は**索引テキスト**であり、突合（全文一致）にはそのまま使うが、
    // `SearchResultDto.Text` は `DocumentBodyPresence.Excerpt` を通して**本文なしの点では空**にする。
    // ここを素朴に `c.Text` で埋めると、**テストは緑のまま本番だけが正しい**（あるいはその逆の）
    // 状態になる —— [[IADR-0014]] が ABAC 属性で、#642 がタグで踏んだのと同型である。
    private static SearchResultDto ToResult(ChunkPayload c, float score) =>
        new(c.ChunkId, c.DocumentId, c.DocumentTitle,
            DocumentBodyPresence.Excerpt(c.Text, c.HasBody), score, c.MarkdownUri,
            c.Attributes, c.Tags, c.UpdatedAt, c.HasBody);

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
        ScopeFilter? filters, CancellationToken ct = default)
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
            .Select(x => ToResult(x.Chunk, x.Hits))
            .ToList();

        return Task.FromResult(results);
    }

    // FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（テスト/ローカル用の等価実装）。
    // **Qdrant 実装と同じ意味論**にする——同じ ABAC フィルタで絞った集合から、値集合だけを返す
    // （件数は返さない。ADR-0043 決定 2 / [[IADR-0151]] 決定 2）。
    public Task<List<string>> ListAttributeValuesAsync(
        string payloadKey, ScopeFilter? filters, CancellationToken ct = default)
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
    //
    // FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: 分岐があれば**分岐内 AND・分岐間 OR**で
    // 評価し、連言（利用者指定の絞り込み）とは AND で重ねる。**Qdrant 側の写像と同じ意味論**である
    // （BuildAttributeConditions / BuildBranchDisjunction。ずれると「テストは緑・本番は別物」になる。
    // IADR-0014 が実際に踏んだ型）。
    //
    // 🔴 **キー単位 union へ畳まない**（IADR-0253 決定 2 の反例）。
    private static bool MatchesFilters(ChunkPayload c, ScopeFilter? filters)
    {
        if (filters is null || filters.IsUnconstrained)
            return true;

        if (!MatchesAll(c, filters.Conjunction))
            return false;

        if (!filters.HasBranches)
            return true;

        // 文書条件を持たない分岐は「そのポリシーの範囲で全件許可」＝選言は制約にならない
        // （Qdrant 側が選言そのものを省くのと同じ扱い）。
        //
        // 🔴 FR-19, ADR-0061 決定 5・6, [[IADR-0394]] 決定 7 (#1184):
        // **個人資料を許可してよいのは裁量（`owner` / `shared_with`）の分岐だけ**である。
        // 静的属性ベースの分岐（`confidentiality ∈ {restricted}` 等）は、露出 ON の個人資料を
        // そのまま許可してしまう —— `restricted` クリアランスを持つ他人に他人の個人メモが見える。
        // 判定は `PrivateNoteVisibility` の 1 か所（Qdrant 側・Graph 側と同じ述語）。
        return filters.Branches!.Any(b =>
            (b.Count == 0 || MatchesAll(c, b)) && PrivateNoteVisibility.BranchMayGrant(c.Attributes, b));
    }

    // FR-05: フィルタ間 AND、値集合内 OR。属性キーを持たない文書は不一致（安全側）。
    private static bool MatchesAll(ChunkPayload c, IReadOnlyList<AttributeFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return true;

        return filters.All(f => MatchesOne(c, f));
    }

    // FR-05, FR-19, #1184: 1 つの `key ∈ AllowedValues` を評価する。
    // **リスト項目（`tags` / `shared_with`）は「いずれか一致」、属性は単一値の完全一致**である。
    // 🔴 **どのキーがリストかを自前で書かない** —— `AttributeValueKeys.IsListValued` へ寄せて
    // Qdrant 側と 1 つの意味論に保つ（ずれると「テストは緑・本番は別物」になる。[[IADR-0014]] の型）。
    private static bool MatchesOne(ChunkPayload c, AttributeFilter f)
    {
        var payloadKey = AttributeValueKeys.ToPayloadKey(f.Key);
        if (!AttributeValueKeys.IsListValued(payloadKey))
        {
            return c.Attributes.TryGetValue(f.Key, out var v)
                   && f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase);
        }

        var values = payloadKey == AttributeValueKeys.Tags
            ? c.Tags
            : c.SharedWith ?? (IReadOnlyList<string>)[];

        return values.Any(v => f.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase));
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
