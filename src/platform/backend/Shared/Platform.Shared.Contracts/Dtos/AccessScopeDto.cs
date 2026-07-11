namespace Platform.Shared.Contracts.Dtos;

// FR-05, UC-05: ABAC 権限スコープ解決用 DTO
public record AccessScopeRequest(
    string UserId,
    Dictionary<string, string> UserAttributes);

// FR-05: スコープ解決結果。
// Granted = 利用者にマッチするポリシーが 1 つでもあったか（deny-by-default の判定材料）。
//   false の場合、許可ポリシーが無い＝閲覧可能文書なし（フィルタが空でも「全件開放」ではない）。
// AllowedFilters が空 かつ Granted=true は「条件無しで許可（全件可）」を意味する。
public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters,
    bool Granted = false);

// FR-05: 1 つの属性キーに対する許可値集合（key ∈ AllowedValues）。
public record AttributeFilter(string Key, List<string> AllowedValues);

// FR-05: 検索へ渡す ABAC アクセススコープ（多値 allow-list ＋ アクセス可否）。
//   GrantsAccess=false → 閲覧可能文書なし（検索は何も返さない＝deny-by-default）。
//   Filters の各要素は「文書の属性 key の値が AllowedValues に含まれること」を要求し、
//   フィルタ間は AND、値集合内は OR で評価する。
public record AccessScope(
    List<AttributeFilter> Filters,
    bool GrantsAccess);
