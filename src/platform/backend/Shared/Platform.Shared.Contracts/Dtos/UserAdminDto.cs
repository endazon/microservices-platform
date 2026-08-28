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

// SC-17 入力/バリデーション: ABAC 属性（部門・機密区分上限・タグ）の割当。
// **差し替えである**（部分更新ではない）。送らなかったキーは消える。
public record ReplaceUserAttributesRequest(Dictionary<string, string> Attributes);

// SC-17 入力/バリデーション: ロール割当（必須・複数選択・定義済みロールのみ・併任可）。
// **差し替えである**（送った集合が、その利用者の realm ロールの全体になる）。
public record ReplaceUserRolesRequest(List<string> Roles);
