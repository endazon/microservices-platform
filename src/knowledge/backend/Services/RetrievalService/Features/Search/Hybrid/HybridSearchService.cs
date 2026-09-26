using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using RetrievalService.Domain;
using RetrievalService.Domain.Ports;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, UC-01: ベクトル検索と全文検索を Reciprocal Rank Fusion で統合するハイブリッド検索
//
// FR-03, FR-05, ADR-0092 決定 1・2・3, [[IADR-0467]] (#336): **分離したコレクションを 1 回の検索で束ねる。**
// 主コレクション（`store` / `embed`）に加え、`fused` の各コレクションでもベクトル系統と全文系統を引き、
// **全系統を 1 回の RRF に平らに入れる**（並び: 主ベクトル → 主全文 → 追加 1 ベクトル → 追加 1 全文 → …）。
// 🔴 **スコアを比べない**（モデルが違えばスコアは同じ意味を持たない）。定数は下の `RrfK`・候補幅・重み 1 を共用する。
// 🔴 **ABAC フィルタは全コレクションの全系統へ同じ 1 本を渡す**（決定 3。問い合わせを省いて守る設計にしない）。
// 🔴 **`fused` が空（既定）なら、全モードで従来と 1 バイトも違わない**（戻り値・Score・呼び出し回数）。
public class HybridSearchService(
    IVectorStore store, IEmbeddingService embed, ILogger<HybridSearchService> logger,
    FusedCollections? fused = null)
    : IHybridSearchService
{
    // RRF の平滑化定数（順位ベース統合。上位の影響を緩める一般的な既定値）
    // [[IADR-0467]] 決定 2: コレクションを束ねる融合も**同じ値**を使う（系統ごとに k を変えない）。
    internal const int RrfK = 60;

    // 束ねる追加コレクション（既定は空）。
    private readonly IReadOnlyList<FusedCollection> _fused = fused?.Items ?? [];

    // FR-03, UC-01: 既存の呼び出し面。**振る舞いは従前と 1 バイトも変わらない。**
    //
    // 🔴 `user` は**段が無いこの実装では使わない**（[[IADR-0426]] 決定 2）。
    // それでもポートが必須引数で受けるのは、**段を挟んだ瞬間に必要になるもの**を
    // 呼び出し側へ先に要求しておくためである —— 器から拾わせると入口ごとに主体が変わる。
    public async Task<List<SearchResultDto>> SearchAsync(
        SearchRequest request, SearchUserContext user, CancellationToken ct = default)
    {
        var outcome = await SearchDetailedAsync(request, ct);
        return Finish(outcome.Fused, outcome.Sort, outcome.TopK);
    }

    // FR-04, FR-17, ADR-0035 決定 1 (#970): 二段検索の段が要る**中間値**を添えて返す内部口。
    //
    // 🔴 **既存検索のアルゴリズムは変えていない**（ADR-0035 決定 1「既存検索の実装は変更せず、
    // 後段を足す」）。変えたのは「途中の値を返すかどうか」だけで、`SearchAsync` の観測可能な
    // 振る舞い（戻り値・埋め込みの呼び出し回数・ストアの呼び出し回数）は同一である。
    //
    // **なぜ後段（デコレータ）が自前でやり直さないのか。**
    //   - 段② の起点は **ベクトル側の順位**である（ADR-0035 決定 2。融合結果ではない）
    //   - 段③ は `queryVector` と **同じ ABAC フィルタ**を要求する（IADR-0259 決定 3）
    // やり直すと、**1 回の検索で埋め込みを 2 回呼ぶ**うえ `BuildFilters` の真実源が 2 つに割れる。
    internal async Task<HybridSearchOutcome> SearchDetailedAsync(
        SearchRequest request, CancellationToken ct = default)
    {
        // FR-03, SC-02, #532: 並び順（2 値）。未知・未指定は既定（relevance）へ縮退する。
        // **取得後に並べ替える**（IADR-0150 決定 1）——関連度が候補を決め、日時は表示順だけを決める。
        // **早期復帰でも同じ値を返す**ため、経路の先頭で 1 度だけ正規化する。
        var sort = SearchSorts.Normalize(request.SortBy);

        if (string.IsNullOrWhiteSpace(request.Query))
            return HybridSearchOutcome.Empty(sort, request.TopK);

        // FR-05: deny-by-default（fail-closed）。IADR-0012。
        //   Scope 未指定（null）＝呼び出し側が ABAC スコープを解決していない、
        //   GrantsAccess=false＝許可ポリシーが無い（閲覧可能文書なし）。
        //   いずれも「何も返さない」に倒す。GrantsAccess=true の明示的許可がある時だけ検索する。
        //   ここを null 許容にすると Scope 無しの呼び出しがフィルタ無しで全文書を返し、
        //   ネットワーク到達可能な相手が ABAC を全面バイパスできてしまう（呼び出し側 Scope の無検証信任）。
        if (request.Scope is not { GrantsAccess: true })
            return HybridSearchOutcome.Empty(sort, request.TopK);

        // FR-05: 単値フィルタ（後方互換）と ABAC 多値スコープを 1 本の allow-list に正規化する。
        var filters = BuildFilters(request);

        // 融合精度のため topK より広めの候補を各系統から取得する
        var candidateK = Math.Max(request.TopK * 4, request.TopK);

        // FR-03, SC-02, #531: 検索モード（3 値）で使う系統を選ぶ。未知・未指定は hybrid へ縮退する。
        var mode = SearchModes.Normalize(request.Mode);

        // 単系統のときは候補を広げる意味が無い（融合しないため）ので topK をそのまま使う。
        // **ただし日時順のときは広げる**（IADR-0150 決定 3）——並べ替える以上、候補が広いほど
        // 「もっと新しい関連文書」を拾える。融合と同じ幅に揃える（同じ検索で 2 つの候補幅を持たない）。
        var singleModeK = sort == SearchSorts.Updated ? candidateK : request.TopK;

        if (mode == SearchModes.Keyword)
        {
            // 全文検索だけの経路。**ベクトル側が無いので段② の起点も無い**（起点はベクトル側のみ）。
            if (_fused.Count == 0)
                return new HybridSearchOutcome(
                    await store.KeywordSearchAsync(request.Query, singleModeK, filters, ct),
                    [], [], filters, sort, request.TopK, candidateK);

            // FR-03, ADR-0092 実測 6・決定 1, [[IADR-0467]] (#336): **全文も束ねる。**
            // 全文検索もコレクションを読む（scroll）ので、主だけを引くとティア A の文書は
            // キーワードでも見つからない。各コレクションの全文の並びを順位で合成する。
            var keywordTasks = new List<Task<List<SearchResultDto>>>(1 + _fused.Count)
            {
                store.KeywordSearchAsync(request.Query, singleModeK, filters, ct)
            };
            keywordTasks.AddRange(_fused.Select(f =>
                f.Store.KeywordSearchAsync(request.Query, singleModeK, filters, ct)));
            await Task.WhenAll(keywordTasks);

            return new HybridSearchOutcome(
                ReciprocalRankFusion(keywordTasks.Select(t => (IReadOnlyList<SearchResultDto>)t.Result).ToArray()),
                [], [], filters, sort, request.TopK, candidateK);
        }

        if (mode == SearchModes.Semantic && _fused.Count > 0)
            return await SemanticAcrossCollectionsAsync(
                request, filters, sort, singleModeK, candidateK, mode, ct);

        if (mode == SearchModes.Semantic)
        {
            var semanticVector = await embed.EmbedAsync(request.Query, ct);
            // FR-03, ADR-0016, #995: 埋め込みが得られないなら、意味検索は**行えない**。
            // 全文へ振り替えると利用者が選んだモードを勝手に変えることになるので 0 件で返す（HTTP 200）。
            if (semanticVector.Length == 0)
            {
                WarnEmbeddingUnavailable(mode);
                return HybridSearchOutcome.Empty(sort, request.TopK);
            }

            var semanticHits = await store.SearchAsync(semanticVector, singleModeK, filters, ct);
            return new HybridSearchOutcome(
                semanticHits, semanticHits, semanticVector, filters, sort, request.TopK, candidateK);
        }

        // FR-03: 意味検索（ベクトル）と全文検索（キーワード）を並行実行し p95 を抑える
        // FR-03, ADR-0092 決定 2, [[IADR-0467]] (#336): 追加コレクションのクエリは**そのコレクションのモデルで**
        // 埋める（主と並行に呼ぶ。追加が空なら主の 1 回だけで、従来と同じ呼び出しである）。
        var (vector, fusedVectors) = await EmbedQueryAsync(request.Query, ct);

        // FR-03, ADR-0016, #995: 🔴 **空ベクトルを後段（ベクトルDB）へ渡さない。**
        // `/embed` は送信拒否（fail-closed）・次元不整合・呼び出し失敗のいずれでも
        // **200 ＋ 空ベクトル（`Embedded=false`）** を返す設計であり、これは**故障ではなく縮退**である。
        // それを次元付きコレクションへ投げると Qdrant が `RpcException` を返し、`/search` は
        // **本文なしの 500** になっていた（#995。統合スタックの段 11 が実測）。
        // **`InMemoryVectorStore` は `queryVector` を参照しないためテストは緑のまま**で、
        // [[IADR-0014]] が記録した「テストは緑・本番は壊れている」と同型であった。
        //
        // 全文検索が使えないときにベクトルのみへ降りる（`QdrantVectorStore.KeywordSearchAsync`）のと
        // **対称に**、意味検索が使えないときは全文のみで続ける。**空にはしない。**
        if (vector.Length == 0)
            WarnEmbeddingUnavailable(mode);

        var vectorTask = vector.Length > 0
            ? store.SearchAsync(vector, candidateK, filters, ct)
            : Task.FromResult(new List<SearchResultDto>());
        var keywordTask = store.KeywordSearchAsync(request.Query, candidateK, filters, ct);

        // FR-03, FR-05, ADR-0092 決定 1・3, [[IADR-0467]] (#336): 追加コレクションも**同じ候補幅・同じフィルタ**で
        // 両系統を引く。🔴 **フィルタを省かない・緩めない** —— 利用者のスコープが高機密を許さなくても
        // 問い合わせは省かず、権限外の点はフィルタが落とす（省略を統制の担い手にしない）。
        var fusedTasks = _fused
            .Select((f, i) => SearchCollectionAsync(f, fusedVectors[i], request.Query, candidateK, filters, mode, ct))
            .ToList();
        await Task.WhenAll(new Task[] { vectorTask, keywordTask }.Concat(fusedTasks));

        // FR-03: 順位ベースで両系統を統合（スコアのスケール差を正規化なしで吸収）
        // ADR-0092 決定 1: 追加コレクションの系統も**同じ 1 回の RRF に平らに**入れる。
        // 追加が空なら `RRF(主ベクトル, 主全文)` —— 従来と同じ式である。
        var rankings = new List<IReadOnlyList<SearchResultDto>> { vectorTask.Result, keywordTask.Result };
        foreach (var t in fusedTasks)
        {
            rankings.Add(t.Result.Vector);
            rankings.Add(t.Result.Keyword);
        }

        var fusedResults = ReciprocalRankFusion(rankings.ToArray());
        // 段②③（二段検索）の起点は**主コレクションのベクトル側のまま**である（[[IADR-0467]] 決定 5）。
        return new HybridSearchOutcome(
            fusedResults, vectorTask.Result, vector, filters, sort, request.TopK, candidateK);
    }

    // FR-03, ADR-0092 決定 2, [[IADR-0467]] (#336): 主と追加コレクションのクエリ埋め込みを並行に得る。
    // **追加が空なら主の 1 回だけ**（従来と同じ呼び出し）。輸送の失敗は潰さずに上げる（[[IADR-0256]] 決定 3）。
    private async Task<(float[] Primary, float[][] Fused)> EmbedQueryAsync(string query, CancellationToken ct)
    {
        var primary = embed.EmbedAsync(query, ct);
        if (_fused.Count == 0)
            return (await primary, []);

        var fusedEmbeds = _fused.Select(f => f.Embed.EmbedAsync(query, ct)).ToArray();
        await Task.WhenAll(fusedEmbeds.Prepend(primary));
        return (primary.Result, fusedEmbeds.Select(t => t.Result).ToArray());
    }

    // FR-03, FR-05, ADR-0092 決定 1・3, [[IADR-0467]] (#336): 追加コレクション 1 つ分の両系統。
    // 埋め込めなかったコレクションはベクトル系統だけを落とし、全文は引く（主の #995 と同じ縮退の向き）。
    private async Task<(List<SearchResultDto> Vector, List<SearchResultDto> Keyword)> SearchCollectionAsync(
        FusedCollection collection, float[] vector, string query, int k, ScopeFilter filters,
        string mode, CancellationToken ct)
    {
        if (vector.Length == 0)
            WarnFusedEmbeddingUnavailable(collection.Collection, mode);

        var vectorTask = vector.Length > 0
            ? collection.Store.SearchAsync(vector, k, filters, ct)
            : Task.FromResult(new List<SearchResultDto>());
        var keywordTask = collection.Store.KeywordSearchAsync(query, k, filters, ct);
        await Task.WhenAll(vectorTask, keywordTask);
        return (vectorTask.Result, keywordTask.Result);
    }

    // FR-03, ADR-0092 決定 1, [[IADR-0467]] (#336): semantic モードを束ねる経路（追加コレクションがあるときだけ通る）。
    //
    // 埋め込めたコレクションのベクトル系統だけを RRF で合成する。🔴 **どのコレクションも埋め込めなければ 0 件**
    // （従来の semantic と同じ意味。全文へ振り替えて利用者が選んだモードを勝手に変えない）。
    private async Task<HybridSearchOutcome> SemanticAcrossCollectionsAsync(
        SearchRequest request, ScopeFilter filters, string sort, int k, int candidateK,
        string mode, CancellationToken ct)
    {
        var (primaryVector, fusedVectors) = await EmbedQueryAsync(request.Query, ct);
        if (primaryVector.Length == 0)
            WarnEmbeddingUnavailable(mode);
        for (var i = 0; i < _fused.Count; i++)
            if (fusedVectors[i].Length == 0)
                WarnFusedEmbeddingUnavailable(_fused[i].Collection, mode);

        if (primaryVector.Length == 0 && fusedVectors.All(v => v.Length == 0))
            return HybridSearchOutcome.Empty(sort, request.TopK);

        var primaryTask = primaryVector.Length > 0
            ? store.SearchAsync(primaryVector, k, filters, ct)
            : Task.FromResult(new List<SearchResultDto>());
        var fusedTasks = _fused
            .Select((f, i) => fusedVectors[i].Length > 0
                ? f.Store.SearchAsync(fusedVectors[i], k, filters, ct)
                : Task.FromResult(new List<SearchResultDto>()))
            .ToList();
        await Task.WhenAll(fusedTasks.Prepend(primaryTask));

        var rankings = new List<IReadOnlyList<SearchResultDto>> { primaryTask.Result };
        rankings.AddRange(fusedTasks.Select(t => (IReadOnlyList<SearchResultDto>)t.Result));
        return new HybridSearchOutcome(
            ReciprocalRankFusion(rankings.ToArray()), primaryTask.Result, primaryVector,
            filters, sort, request.TopK, candidateK);
    }

    // FR-03, ADR-0092 決定 1, [[IADR-0467]] (#336): 追加コレクションだけ埋め込めないときの痕跡。
    // 主の縮退（`WarnEmbeddingUnavailable`）と**別の文言**にする —— どのコレクションが落ちたかが
    // ログから読めないと、ティア A の推論基盤の不調と voyage の不調が区別できない。
    private void WarnFusedEmbeddingUnavailable(string collection, string mode) =>
        logger.LogWarning(
            "Query embedding unavailable for fused collection {Collection}; "
            + "searching it in mode {Mode} without its semantic channel", collection, mode);

    // FR-03, ADR-0016, #995: 🔴 **静かに縮退しない。** 「検索は 200 なのに意味検索が効いていない」は
    // 応答からは区別できない（`SearchResponse` は縮退の有無を持たない）。**ログだけが手掛かりである。**
    // 原因（送信拒否か・キー未設定か・不調か）はゲートウェイ側が `RoutingReason` 付きで記録している。
    private void WarnEmbeddingUnavailable(string mode) =>
        logger.LogWarning(
            "Query embedding unavailable (empty vector from LLM gateway); "
            + "degrading search mode {Mode} without the semantic channel", mode);

    // FR-03, SC-02, #532: 並び順を適用して topK 件へ切る（IADR-0150 決定 1・3・4）。
    //
    // **relevance（既定）は取得順のまま**——現行の振る舞いを一切変えない。
    // **updated は更新日時の降順**で、**日時を持たないチャンク（未再索引・IADR-0149 決定 3）は末尾**へ置く。
    // 「新しい順」の先頭が日時不明で埋まらないようにするためであり、**DateTimeOffset.MinValue へ
    // 倒さない**——倒すと比較子が嘘を持ち、「本当に古い文書」と区別できなくなる。
    //
    // **OrderByDescending は安定ソート**である（.NET の保証）。同着（同じ日時・日時なし同士）は
    // 元の順序＝関連度の順を保つ。
    // 🔴 FR-19, ADR-0061 決定 1・3 / [[IADR-0396]] 決定 6 (#1184): **「横断検索に含める」の評価点。**
    //
    // ADR-0061 決定 1 は「1 つでも ON なら索引へ載せる」であり、決定 3 は
    // 「**用途の別は索引を分けずに文書属性で表す**」である。したがって
    // **グラフや AI のためだけに索引へ載った個人資料が、横断検索の結果に現れてはならない。**
    // 判定は `DocumentExposure.IsSearchAllowed` —— 生産側の門と同じクラスの、同じ形の述語である。
    //
    // 🔴 **これは ABAC の代わりではない。** 認可（誰に見えるか）は `ScopeFilter` の分岐が索引の
    // 側で行い、ここが見るのは**露出の用途**（何に使ってよいか）だけである。
    // **`confidentiality` だけで判定してはならない**（決定 6）というのは前者の話であり、
    // ここを足したことで認可が緩むことはない（絞る向きにしか働かない）。
    //
    // **なぜ `Finish` なのか。** 結果の一覧を返す口が**ここ 1 つに集まっている**
    // （`SearchAsync` と `GraphExpandingSearchService` の 3 つの return がすべて通る）。
    // 経路ごとに書くと、後から段を足した人が落としても誰も気づかない。
    // **切り詰め（`topK`）より前に落とす** —— 後だと除外した分だけ結果が減る。
    //
    // **組織文書は常に true**（露出キーを持たない）なので既存の検索結果は変わらない。
    internal static List<SearchResultDto> Finish(
        List<SearchResultDto> results, string sort, int topK)
    {
        var exposed = results.Where(r => DocumentExposure.IsSearchAllowed(r.Attributes)).ToList();

        if (sort != SearchSorts.Updated)
            return exposed.Take(topK).ToList();

        return exposed
            .OrderByDescending(r => r.UpdatedAt.HasValue)   // 日時なしを末尾へ（null-last を明示する）
            .ThenByDescending(r => r.UpdatedAt ?? default)  // 日時ありの中で新しい順
            .Take(topK)
            .ToList();
    }

    // FR-05: 単値 AttributeFilters（FR-03 後方互換）と ABAC 多値 Scope を検索の制約へ写す。
    //
    // FR-19, ADR-0036, IADR-0253 決定 1（段 3 / #989）: スコープが**名前つき分岐**を運んでいれば
    // **分岐間 OR・分岐内 AND** で評価する。1 分岐 = マッチした 1 ポリシーの文書条件であり、
    // 計画の read 規則「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の選言を写す。
    //
    // 🔴 **分岐をキー単位 union へ畳まない**（IADR-0253 決定 2 の反例）——
    // A={confidentiality:internal, department:hr} と B={confidentiality:public, department:sales}
    // を union すると、**どちらのポリシー単独も許可しない混成 (internal, sales) を許す**。
    //
    // 利用者指定の AttributeFilters は**分岐の選言全体と AND** で重ねる（絞り込みは narrowing であり
    // 権限を広げない）。**分岐が無いときは従来の算出（キー単位の結合）をそのまま使う**——
    // 未移行の発行者から来た応答の互換のためであり、値も意味も変えない。
    // FR-03, FR-05, NFR-09, SC-01, SC-08, ADR-0004, [[IADR-0151]], [[IADR-0253]] 決定 1・2,
    // [[IADR-0415]] (#1340): 利用者指定と ABAC 許可スコープを 1 本の allow-list へ正規化する。
    //
    // 🔴 **利用者指定は絞るだけで広げない。** 従前ここは非分岐経路で利用者指定と ABAC 許可を
    // **同じキーの下へ union して**おり、**指定するだけで許可値集合が広がっていた** ——
    // 許可 `dept ∈ {sales}` に指定 `dept=hr` を送ると `{sales, hr}` になり、
    // **認証済み利用者が権限外の文書を読めた**（#1340 で実測）。しかも絞り込みとしても
    // 効いていなかった（許可 `{sales, eng}` ＋ 指定 `sales` で eng が残った）。
    //
    // 🔴 **規則は共有点が 1 つだけ持つ**（`ScopeNarrowing`）—— 呼び出し元側（AiAnalysis の
    // データ範囲）と**同じ関数**を通る。規則が片方にしか無かったことが欠陥の正体だった。
    private static ScopeFilter BuildFilters(SearchRequest request)
    {
        // 🔴 **交差を先に済ませる。** 実効スコープは ABAC 許可の部分集合であり、
        // 積が空のキーがあれば `ScopeNarrowing` が全体 deny（`GrantsAccess=false`）を返す。
        var effective = ScopeNarrowing.Apply(request.Scope, request.AttributeFilters);

        // 全体 deny は「1 件も選ばない」フィルタでなければならない。
        // 🔴 **`ScopeFilter.Empty`（＝制約なし）へ倒してはならない** —— 全件開放になる。
        if (!effective.GrantsAccess)
            return DenyEverything;

        if (effective is { Branches.Count: > 0 } scoped)
            // 🔴 **分岐経路では利用者指定を選言全体と AND で重ねる**（従前どおり。元から正しい）。
            // `ScopeNarrowing` は分岐ごとに独立して交差させ、積が空の分岐だけを捨てている。
            return new ScopeFilter(
                [],
                [.. scoped.Branches.Select(b => (IReadOnlyList<AttributeFilter>)b.Filters)]);

        return effective.Filters.Count == 0
            ? ScopeFilter.Empty
            : new ScopeFilter([.. effective.Filters]);
    }

    // 🔴 **1 件も選ばないフィルタ。** 「制約なし」と取り違えると全件開放になる。
    // 実在しないキーの実在しない値を要求する（どの文書にも一致しない）。
    private static readonly ScopeFilter DenyEverything =
        new([new AttributeFilter("__deny__", ["__none__"])]);

    // FR-03: Reciprocal Rank Fusion。両リストに現れる文書ほど上位になる。
    //
    // FR-03, ADR-0092 決定 1, [[IADR-0467]] 決定 2 (#336): コレクションを束ねる融合も**この関数 1 つ**を通る。
    // 🔴 **入力の `Score` は読まない**（順位だけを見る）。モデルが違えばスコアは同じ意味を持たない。
    // 🔴 **同点は「先に現れた方が先」**（入力の並び順 → 各並びの中の順位）。従来は `Dictionary` の
    // 列挙順に暗黙に依存していたので、同じ順序を**二次キーとして明示した**（並びは従来と同一）。
    // 重みは全系統 1（系統ごとの重み付けはしない。入れるなら nDCG で測ってから IADR を改める）。
    internal static List<SearchResultDto> ReciprocalRankFusion(
        params IReadOnlyList<SearchResultDto>[] rankings)
    {
        var scores = new Dictionary<Guid, double>();
        var byId = new Dictionary<Guid, SearchResultDto>();
        var firstSeen = new Dictionary<Guid, int>();

        foreach (var ranking in rankings)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var hit = ranking[rank];
                scores[hit.ChunkId] = scores.GetValueOrDefault(hit.ChunkId) + 1.0 / (RrfK + rank + 1);
                // 最初に出会ったペイロードを採用（出典情報は同一チャンクで一致）
                byId.TryAdd(hit.ChunkId, hit);
                firstSeen.TryAdd(hit.ChunkId, firstSeen.Count);
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => byId[kv.Key] with { Score = (float)kv.Value })
            .ToList();
    }
}

// FR-04, FR-17, ADR-0035 決定 1・2 (#970): ハイブリッド検索の結果と、二段検索の段が要る中間値。
//
// **`Fused` は並び替え・切り詰めの前**である（`Finish` を通す前）。再ランクは候補が広いほど
// 意味を持つため、段は広い候補集合を受け取り、**最後に一度だけ** `Finish` で並べて切る。
internal sealed record HybridSearchOutcome(
    List<SearchResultDto> Fused,
    // ADR-0035 決定 2「展開の起点は**ベクトル検索の上位 N 件のみ**（全文検索側は起点にしない）」。
    // 融合後の順位からは「どちらの系統が見つけたか」が読めないので、ベクトル側を別に持つ。
    List<SearchResultDto> VectorSide,
    // 段③（文書 ID 制約つき検索）が同じベクトルで採点するために要る。埋め込みを 2 度呼ばない。
    float[] QueryVector,
    // 段③の ABAC フィルタ。**文書 ID の制約は ABAC を置き換えない**（AND。IADR-0259 決定 3）。
    // FR-19 / #989 段 3: 分岐（選言）も運ぶ —— 段③ が段① と**同じ制約**で絞るためである
    // （連言だけ渡すと段③ だけがキー単位 union で判定し、混成を許してしまう）。
    ScopeFilter? Filters,
    string Sort,
    int TopK,
    int CandidateK)
{
    // 早期復帰（空クエリ・スコープ無し・埋め込み不能）。**段も何もできない**（起点が無い）。
    public static HybridSearchOutcome Empty(string sort, int topK) =>
        new([], [], [], null, sort, topK, topK);
}
