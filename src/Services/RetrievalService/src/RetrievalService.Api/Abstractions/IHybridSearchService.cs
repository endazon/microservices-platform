using KnowledgePlatform.Shared.Contracts.Dtos;

namespace RetrievalService.Api.Abstractions;

// FR-03, UC-01: ハイブリッド検索（ベクトル＋全文）のポート
public interface IHybridSearchService
{
    Task<List<SearchResultDto>> SearchAsync(SearchRequest request, CancellationToken ct = default);
}
