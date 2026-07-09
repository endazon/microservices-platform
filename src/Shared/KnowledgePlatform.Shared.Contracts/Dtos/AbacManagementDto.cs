namespace KnowledgePlatform.Shared.Contracts.Dtos;

// FR-09, UC-05, SC-09: ABAC 管理（属性辞書・ポリシー）の参照用 DTO（BFF ↔ SPA 契約）。
// AuthorizationService のドメインエンティティ（/authz/policies・/authz/attributes）と JSON 互換。
// BFF は AdminOnly 集約点でこれらを中継する（書き込みは検証結果 400／参照競合 409 を透過する）。

// FR-09: ABAC ポリシー（利用者属性 × 文書属性 → 許可アクション）。
public record AbacPolicyDto(
    Guid Id,
    string Name,
    string Action,
    Dictionary<string, List<string>> UserConditions,
    Dictionary<string, List<string>> DocumentConditions,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// FR-09: 属性辞書エントリ（管理者が定義する取りうる値）。
public record AttributeDefinitionDto(
    Guid Id,
    string Key,
    string Label,
    List<string> AllowedValues,
    bool Required,
    string Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
