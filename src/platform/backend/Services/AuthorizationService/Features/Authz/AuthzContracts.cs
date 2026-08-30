namespace AuthorizationService.Features.Authz;

// FR-05, FR-09, UC-05: ABAC 管理 API の要求・応答 DTO。
//
// ADR-0065 決定 2: **複数の操作が共有するため集約直下に置く。**
// `CreatePolicyRequest` は登録（POST）・更新（PUT）・dry-run 検証（POST /policies/validate）の
// **3 操作が同じ型を使う** —— 画面が保存用と検証用で 2 つの組み立てを持つと、そこがズレる（#535）。

public record CreatePolicyRequest(
    string Name,
    string Action,
    Dictionary<string, List<string>> UserConditions,
    Dictionary<string, List<string>> DocumentConditions);

public record CreateAttributeRequest(
    string Key,
    string Label,
    List<string> AllowedValues,
    bool Required,
    string? Scope);

// 属性辞書更新（Key / Scope は不変のため受け取らない）
public record UpdateAttributeRequest(
    string Label,
    List<string> AllowedValues,
    bool Required);

public record SetActiveRequest(bool IsActive);

public record ValidateDocumentAttributesRequest(Dictionary<string, string> Attributes);

public record ValidateDocumentAttributesResponse(bool Valid, List<string> Errors);
