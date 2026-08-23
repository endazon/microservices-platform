using GraphService.Api.Foundation.Ports;

namespace GraphService.Api.Composable.Adapters;

// FR-18, ADR-0051 決定 1, IADR-0266 論点 C (#915): 類似度候補の**既定アダプタ**。常に空を返す。
//
// 🔴 **これは「未実装の穴埋め」ではなく、供給元が無いことを明示する既定である。**
// ADR-0051 決定 1 は「埋め込み類似度の算出は ABAC スコープを跨いで行ってよい」と認めたが、
// **その越境してよい口が RetrievalService に無い** —— 同サービスの検索口はいずれも ABAC フィルタ付きで、
// 「全文書横断の類似度」を返す経路が存在しない（実測）。同サービスは別 issue の作業領域である。
//
// **空を返すことは fail-closed 側である**（候補が無ければ LLM へは何も送られず、提案も 0 件になる）。
// 口ができた時点で本アダプタを差し替えるだけで済むよう、ポートは切ってある。
public sealed class UnconfiguredSimilarityCandidateSource(
    ILogger<UnconfiguredSimilarityCandidateSource> logger) : ISimilarityCandidateSource
{
    public Task<IReadOnlyList<SimilarityCandidate>> FindSimilarAsync(
        Guid originDocumentId, int limit, CancellationToken ct = default)
    {
        // 🔴 **起点の ID しか出さない。** 候補の件数・存在は出さない（ADR-0051 決定 2）。
        // ここで返すのは常に空なので漏らすものは無いが、差し替え後も同じ作法を保つこと。
        logger.LogDebug(
            "Similarity candidate source is not configured; returning no candidates. origin={OriginDocumentId}",
            originDocumentId);
        return Task.FromResult<IReadOnlyList<SimilarityCandidate>>([]);
    }
}
