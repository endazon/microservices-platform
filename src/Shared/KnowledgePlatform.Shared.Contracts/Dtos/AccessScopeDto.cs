namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-05, UC-05: ABAC 権限スコープ解決用 DTO
public record AccessScopeRequest(
    string UserId,
    Dictionary<string, string> UserAttributes);

public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters);

public record AttributeFilter(string Key, List<string> AllowedValues);
