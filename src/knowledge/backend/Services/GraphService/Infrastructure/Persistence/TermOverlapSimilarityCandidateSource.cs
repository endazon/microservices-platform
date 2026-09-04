using GraphService.Domain;
using GraphService.Domain.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GraphService.Infrastructure.Persistence;

// FR-18, ADR-0051 決定 1・2, IADR-0380 (#1244): 類似度候補の**既定の供給元**。語の共起で引く。
//
// #1244 の実測: 供給元の実装は「常に空を返す」`UnconfiguredSimilarityCandidateSource` 1 つだけで、
// それが本番 DI に刺さっていたため、FR-18 の提案は**構造的に 0 件**だった。本クラスがその穴を埋める。
//
// 🔴 **ABAC スコープを受け取らない。それが正しい**（ISimilarityCandidateSource の注記）。
// ADR-0051 決定 1 は類似度の算出をスコープを跨いで行ってよいと定めた。**返る候補にはスコープ外の文書が
// 混じる**。絞るのは候補列挙の段（IGraphStore.EnumerateAuthorizedCandidatesAsync）であり、LLM より前である。
//
// 🔴 **ログは起点の ID しか出さない。** 候補の件数・ID・存在を出さない（ADR-0051 決定 2）。
// 差し替え前の既定アダプタが守っていた作法をそのまま引き継ぐ（`SimilaritySourceLoggingTests` が固定する）。
//
// 材料は `graph_document_term_profiles`（本文指紋の変化で作り直される）。**行が無い文書は表題から作る**
// —— 既存文書は初日から候補になり、本文が次に更新された文書から本文入りの出現数へ置き換わる。
//
// 規模: 1 回の生成で全文書の出現数（≤128 語 × 文書数）を読む。生成は利用者の明示操作で LLM 呼び出しを
// 伴う経路であり、数 MB の読み取りは許容する。転置索引（SQL 側で内積）へ移す条件は IADR-0380 §結果。
public sealed class TermOverlapSimilarityCandidateSource(
    GraphDbContext db,
    IOptions<AiSuggestionSimilarityOptions> options,
    ILogger<TermOverlapSimilarityCandidateSource> logger) : ISimilarityCandidateSource
{
    public async Task<IReadOnlyList<SimilarityCandidate>> FindSimilarAsync(
        Guid originDocumentId, int limit, CancellationToken ct = default)
    {
        var profiles = await db.TermProfiles.AsNoTracking()
            .ToDictionaryAsync(p => p.DocumentId, p => p.Terms, ct);
        var documents = await db.Documents.AsNoTracking()
            .Select(d => new { d.DocumentId, d.Title })
            .ToListAsync(ct);

        // 母集合は graph_documents に在る文書（ノードが無い文書はどのみち不可視。IADR-0242 決定 12-3）。
        var corpus = new Dictionary<Guid, IReadOnlyDictionary<string, int>>();
        foreach (var d in documents)
        {
            corpus[d.DocumentId] = profiles.TryGetValue(d.DocumentId, out var terms)
                ? terms
                : TermProfile.Extract(d.Title, null);
        }

        var ranked = TermProfile.Rank(originDocumentId, corpus, options.Value.MinScore, limit);

        logger.LogDebug(
            "Ranked similarity candidates by term overlap. origin={OriginDocumentId}", originDocumentId);

        return ranked.Select(r => new SimilarityCandidate(r.DocumentId, r.Score)).ToList();
    }
}
