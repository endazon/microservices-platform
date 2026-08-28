namespace GraphService.Domain.Ports;

// FR-18, ADR-0051 決定 1: 埋め込み類似度による候補の供給ポート（#915）。
//
// 🔴 **本ポートは ABAC スコープを受け取らない。それが正しい。**
// ADR-0051 決定 1 は「**埋め込み類似度の算出そのものは、ABAC スコープを跨いで行ってよい**」と定めた。
// 理由は ADR-0034 決定 5 の禁止が「**外部への送信**」についてのものだからである —— 類似度計算は
// 自システム内のベクトル演算であり、本文がどこへも出ない。全社共通のベクトル索引を使い回せる。
//
// 🔴 **したがって、返る候補にはスコープ外の文書が混じり得る。**
// それを絞るのは**候補列挙の段**（IGraphStore.EnumerateAuthorizedCandidatesAsync）であり、
// **LLM へ渡す前である**（ADR-0051 決定 3「絞りを LLM 呼び出しより後ろに置いてはならない」）。
// **本ポートの戻り値をそのまま LLM 側へ渡す経路を作らないこと**（型で塞いである。IADR-0266 決定 1）。
public interface ISimilarityCandidateSource
{
    // 起点文書に似た文書を、類似度の降順で最大 limit 件返す。
    // **起点自身を含んでもよい**（呼び出し側が落とす）。
    Task<IReadOnlyList<SimilarityCandidate>> FindSimilarAsync(
        Guid originDocumentId, int limit, CancellationToken ct = default);
}

// FR-18: 類似度の候補。**本文もスニペットも運ばない** —— 運ぶと、スコープ外の本文が
// 呼び出し側のメモリ・ログへ載る経路が生まれる（ADR-0034 決定 1 の具体化と同じ理由）。
public sealed record SimilarityCandidate(Guid DocumentId, double Score);
