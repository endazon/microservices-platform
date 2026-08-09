namespace Platform.Shared.Contracts.Dtos;

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

// FR-05, FR-09, SC-09, #535: ポリシーの dry-run 検証の応答（利用者裁定 2026-08-05 Q23）。
//
// **保存せず検証だけ行った結果である。** 矛盾が見つかっても要求としては成功（200）——
// 「検証した結果、矛盾が 2 件あった」は正常な応答であり、要求の失敗ではない。
// **保存（`POST /policies`）は従来どおり 400 ＋ RFC7807 を返す**（既存の契約は変えない）。
//
// **形は `ValidateDocumentAttributesResponse`（属性の辞書整合検証）と揃えてある**——
// 画面が 2 種類の検証結果の読み方を覚えなくて済む。
//
// **要求型は専用に作らず `CreatePolicyRequest` を再利用する。** 画面が保存用と検証用で
// 2 つの組み立てを持つと、そこがズレる余地になる（ズレると「検証は通ったのに保存で矛盾」に戻る）。
public record ValidatePolicyResponse(bool Valid, List<string> Errors);
