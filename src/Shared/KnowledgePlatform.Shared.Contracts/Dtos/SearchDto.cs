namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-03, UC-01: ハイブリッド検索リクエスト/レスポンス DTO
public record SearchRequest(
    string Query,
    int TopK = 10,
    Dictionary<string, string>? AttributeFilters = null);

public record SearchResponse(
    List<SearchResultDto> Results,
    int TotalHits,
    long ElapsedMs);
