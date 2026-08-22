namespace Platform.Shared.Contracts.Dtos;

// FR-05, UC-05: ABAC 権限スコープ解決用 DTO
public record AccessScopeRequest(
    string UserId,
    Dictionary<string, string> UserAttributes);

// FR-05, FR-19, ADR-0036, ADR-0046 D-06, IADR-0253 決定 1: 認可スコープの 1 分岐。
// **分岐内のフィルタは AND、分岐どうしは OR** で評価する。
//
// Name は監査・デバッグ用の識別子（"attribute" / "owner" / "shared"）。無名の入れ子ではなく
// 名前を持たせるのは、計画 07_abac-attribute-model が「**『ABAC を検証した』と書くときは、
// どの分岐で検証したかを必ず添えること**」と定めているためである（IADR-0253 §検討した選択肢）。
public record AccessScopeBranch(string Name, List<AttributeFilter> Filters);

// FR-05: スコープ解決結果。
// Granted = 利用者にマッチするポリシーが 1 つでもあったか（deny-by-default の判定材料）。
//   false の場合、許可ポリシーが無い＝閲覧可能文書なし（フィルタが空でも「全件開放」ではない）。
// AllowedFilters が空 かつ Granted=true は「条件無しで許可（全件可）」を意味する。
//
// FR-19, ADR-0036, ADR-0046 D-06 部品 3, IADR-0253: Branches は read の選言（OR）を運ぶ。
//   計画の read 規則は「静的属性ベース ∨ 所有者ベース ∨ 共有先ベース」の**選言**だが、
//   AllowedFilters は**単一の連言しか表せない**ため、所有者ベースの判定が成立しなかった。
//
// 評価規則:
//   Granted == false                      → 不可視
//   Granted == true かつ Branches が空/null → **従来どおり AllowedFilters で評価**（後方互換）
//   Branches が 1 件以上                   → **いずれかの分岐のフィルタをすべて満たす文書が可視**
//
// 🔴 **AllowedFilters は算出アルゴリズムごと据え置く**（IADR-0253 決定 2）。未移行のサービスは
//   挙動が 1 ビットも変わらない。**AllowedFilters（分岐の積に相当）は Branches（分岐の和）の
//   部分集合であるため、未移行側が余分に見せることは構造上あり得ない** —— 移行中の乖離は
//   常に deny 側へ倒れる。「気をつける」ではなく包含関係から従う。
//
// **Branches は末尾に置き、既定値 null を付けてある。** 途中へ挿すと位置引数の呼び出しが壊れ、
// 既定値が無いと旧発行者のメッセージが必須項目を欠く（どちらも scripts/check-contract-schema.js
// が破壊的変更として fail させる。IADR-0122 決定 2）。
public record AccessScopeResponse(
    string UserId,
    List<AttributeFilter> AllowedFilters,
    bool Granted = false,
    List<AccessScopeBranch>? Branches = null);

// FR-05: 1 つの属性キーに対する許可値集合（key ∈ AllowedValues）。
public record AttributeFilter(string Key, List<string> AllowedValues);

// FR-05: 検索へ渡す ABAC アクセススコープ（多値 allow-list ＋ アクセス可否）。
//   GrantsAccess=false → 閲覧可能文書なし（検索は何も返さない＝deny-by-default）。
//   Filters の各要素は「文書の属性 key の値が AllowedValues に含まれること」を要求し、
//   フィルタ間は AND、値集合内は OR で評価する。
public record AccessScope(
    List<AttributeFilter> Filters,
    bool GrantsAccess);
