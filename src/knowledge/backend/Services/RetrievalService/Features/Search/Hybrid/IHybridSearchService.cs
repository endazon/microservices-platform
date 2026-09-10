using Knowledge.Contracts.Dtos;
using RetrievalService.Domain;

namespace RetrievalService.Features.Search.Hybrid;

// FR-03, FR-04, FR-05, FR-17, UC-01, UC-10, ADR-0034 決定 1, ADR-0035 決定 2,
// 計画 ADR-0086 決定 1, [[IADR-0410]], [[IADR-0426]] (#1255): ハイブリッド検索（ベクトル＋全文）のポート。
//
// 🔴 **利用者文脈は引数で受け取る。既定値を置かない**（[[IADR-0426]] 決定 2）——
// 二段検索の段（`GraphExpandingSearchService`）が近傍展開へそのまま渡す。
// 器（`IHttpContextAccessor`）から拾わせると、east-west gRPC の入口で
// **呼び出し元サービスの s2s 主体**が利用者に化ける（`SearchUserContext` の表を参照）。
public interface IHybridSearchService
{
    Task<List<SearchResultDto>> SearchAsync(
        SearchRequest request, SearchUserContext user, CancellationToken ct = default);
}
