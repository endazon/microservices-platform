using Knowledge.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-04, SC-02, ADR-0127 決定 3, [[IADR-0498]] 決定 1 (#1746 段 S2): **検索結果の候補を並べ替える段のポート。**
//
// 検索サービスの唯一の出口（`HybridSearchService.FinishAsync`）だけが呼ぶ。受け取るのは
// **ABAC と露出の用途 `search` で落とした後の、切り詰め前の候補**であり、返すのは**同じ候補の並べ替え**である。
// 🔴 **候補を足さない・落とさない。** 実装が返す一覧は入力の置換（同じ要素の並び替え）でなければならない
// —— 並べ替えの外部（LLM）の出力から候補を作ると、ABAC を通っていない文書を混ぜられる。
//
// 段が無い構成（既定）ではこの型を DI に登録しない（二段検索と同じ「着脱可能な段」。ADR-0018）。
public interface ISearchReranker
{
    Task<List<SearchResultDto>> RerankAsync(
        SearchRequest request, string sort, List<SearchResultDto> candidates, CancellationToken ct = default);
}
