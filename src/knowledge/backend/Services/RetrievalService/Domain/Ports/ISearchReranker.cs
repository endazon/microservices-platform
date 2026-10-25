using Knowledge.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-04, SC-02, ADR-0127 決定 3, [[IADR-0498]] 決定 1 (#1746 段 S2): **検索結果の候補を並べ替える段のポート。**
//
// 検索サービスの唯一の出口（`HybridSearchService.FinishAsync`）だけが呼ぶ。受け取るのは
// **ABAC と露出の用途（一覧なら `search`、RAG の候補なら `ai_input`。[[IADR-0512]]）で落とした後の、切り詰め前の候補**であり、
// 返すのは**同じ候補の並べ替え**である。
// 🔴 **候補を足さない・落とさない。** 実装が返す一覧は入力の置換（同じ要素の並び替え）でなければならない
// —— 並べ替えの外部（LLM）の出力から候補を作ると、ABAC を通っていない文書を混ぜられる。
//
// 段が無い構成（既定）ではこの型を DI に登録しない（二段検索と同じ「着脱可能な段」。ADR-0018）。
//
// ［2026-10-11 / #1871］[[IADR-0534]]: 並びに加えて**縮退したか**（掛けようとして元の順へ戻したか）を返す。
// 設計どおり掛けない（`skipped`）は縮退ではない。
public interface ISearchReranker
{
    Task<RerankOutcome> RerankAsync(
        SearchRequest request, string sort, List<SearchResultDto> candidates, CancellationToken ct = default);
}

// FR-03, NFR-06, ADR-0127 決定 3, [[IADR-0534]] (#1871): 段の結果。`Degraded` は応答の `rerank-failed` へ写る。
public sealed record RerankOutcome(List<SearchResultDto> Results, bool Degraded = false);
