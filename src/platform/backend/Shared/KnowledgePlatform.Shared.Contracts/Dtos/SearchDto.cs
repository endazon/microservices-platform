namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-03, UC-01: ハイブリッド検索リクエスト/レスポンス DTO
public record SearchRequest(
    string Query,
    int TopK = 10,
    // FR-03: 単値完全一致フィルタ（後方互換）。key → 単一の許可値。
    Dictionary<string, string>? AttributeFilters = null,
    // FR-05: ABAC アクセススコープ（多値 allow-list ＋ deny-by-default）。
    AccessScope? Scope = null);

public record SearchResponse(
    List<SearchResultDto> Results,
    int TotalHits,
    long ElapsedMs);
