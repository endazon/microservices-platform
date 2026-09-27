namespace Platform.Shared.Contracts.Dtos;

// FR-05, FR-09, UC-05, SC-17, ADR-0026: 利用者アカウント管理（ロール割当・ABAC 属性割当・
// 無効化）の DTO（BFF ↔ SPA 契約）。AuthorizationService の /authz/users* と JSON 互換であり、
// BFF は AdminOnly 集約点で透過中継する。
//
// 🔴 **新規作成の要求型を置かない。** 計画 05_screens §SC-17 アクションは
// 「アカウントは人事システム連携で自動プロビジョニングし（**本画面から新規作成はしない**）」と
// 定めている。作成の DTO が在ると、後から端点を生やす手が伸びる。**契約の側で持たない。**

// SC-17 主要素 1: 利用者一覧の 1 行（部門・ロール・ABAC 属性・状態）。
//
// **部門を独立した列として持たない。** 計画の「部門」は ABAC 属性 `department` そのものであり、
// DTO へ複写すると片方が古くなる（属性を更新して列が追随しない形が作れてしまう）。
// 画面は Attributes から引く。
public record PlatformUserDto(
    string Id,
    string Username,
    string DisplayName,
    bool Enabled,
    List<string> Roles,
    Dictionary<string, string> Attributes);

// SC-17 入力/バリデーション: ABAC 属性（機密区分上限・タグ）の割当。
// **差し替えである**（部分更新ではない）。送らなかったキーは消える。
// ［2026-09-27 / #1610・計画 ADR-0116 決定 1］🔴 **`department` は含めない**（含めると 400）。部門は
// `ReplaceUserDepartmentRequest`（部門グループの所属の変更）で変え、属性は部門の同期が追いつく。差し替えでも現在の部門は消えない。
public record ReplaceUserAttributesRequest(Dictionary<string, string> Attributes);

// SC-17 入力/バリデーション: ロール割当（必須・複数選択・定義済みロールのみ・併任可）。
// **差し替えである**（送った集合が、その利用者の realm ロールの全体になる）。
public record ReplaceUserRolesRequest(List<string> Roles);

// FR-05, FR-09, SC-17, 計画 ADR-0116 決定 1, IADR-0473 (#1610): 利用者の部門（部門グループの所属）と部門欄の選択肢。
// `DepartmentGroups` は利用者が直接属する部門グループのコード（入れ子は上位のコードに畳む。序数順）。
// `DepartmentAttribute` は ABAC が読む利用者属性 `department`（部門の同期が追いつくまでグループと違い得る。無ければ null）。
// `Choices` は realm の `/department` の直下の子のコード（部門欄の選択肢。画面に焼き込まない）。
// 🔴 **部門の正本は部門グループである**（計画 ADR-0115 決定 3）。画面は属性ではなく `DepartmentGroups` を部門欄に出す。
public record UserDepartmentDto(
    List<string> DepartmentGroups,
    string? DepartmentAttribute,
    List<string> Choices);

// FR-05, FR-09, SC-17, 計画 ADR-0116 決定 1, IADR-0473 (#1610): 部門の変更（部門グループの所属の変更）。
// `Department` は選んだ部門コード。null・空は「部門なし」（すべての部門グループから外す）。
// 🔴 **利用者属性 `department` は書かない**（部門の同期が追いつく）。
public record ReplaceUserDepartmentRequest(string? Department);
