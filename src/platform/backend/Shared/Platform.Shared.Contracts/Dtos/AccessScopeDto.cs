namespace Platform.Shared.Contracts.Dtos;

// FR-05, UC-05: ABAC 権限スコープ解決用 DTO
//
// FR-21, ADR-0036 D-07, IADR-0253 決定 5（2026-08-23 改定 / #989）: Action は解決したい
// アクション（read / analyze / manage / write）。従前は /authz/scope がサーバ側で read を
// ハードコードしており、書き込みの認可スコープをこの経路で出せなかった。
//
// **既定値はリテラル "read" である** —— 値域の正（PolicyAction）は AuthorizationService の
// ドメイン型であり、契約プロジェクトからは参照できない。値の一致は評価器側のテストで固定する。
// **Action は末尾に置き、既定値を付けてある**（既定値付き末尾追加＝非破壊。IADR-0122 決定 2）。
// 既存の呼び出し元は無改修で従来どおり read のスコープを得る。
public record AccessScopeRequest(
    string UserId,
    Dictionary<string, string> UserAttributes,
    string Action = "read");

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
//   挙動が 1 ビットも変わらない。
//
//   ［2026-08-23 追記 / #989］**「AllowedFilters は Branches の部分集合であり、未移行側が余分に
//   見せることは構造上あり得ない」という従前の記述は誤りである**（IADR-0253 決定 2 の同日の追記が
//   正本）。反例: 複数のポリシーが**同じ複数キー**を別の値で条件づけると、キー単位の union が
//   **どのポリシー単独も許可しない値の混成**を許す（A=internal×hr、B=public×sales のとき
//   internal×sales の文書）。包含関係は一般には成立しない。
//
//   **今日の実データでは漏れない** —— 実効軸が confidentiality の 1 本しか無いためであり、
//   構造的な保証ではない。したがって**複数キーの文書条件を持つポリシーの運用開始は、
//   全消費側の分岐対応（IADR-0253 段 3）が終わるまで行ってはならない**。
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
