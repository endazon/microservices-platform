namespace Platform.Shared.Contracts.Dtos;

// FR-19, SC-19 主要素 3, ADR-0098 決定 1, planning#618: 共有先の個人指定のための利用者検索の DTO
// （BFF ↔ SPA 契約）。AuthorizationService の `/authz/users/lookup`・`/authz/users/resolve`
// （認証必須・ロール不問）と JSON 互換であり、BFF は利用者の `Authorization` を転送して透過中継する。
//
// 🔴 **管理面（`PlatformUserDto`。AdminOnly の `/authz/users`）とは別の型である。** 一般利用者へ出すのは
// **利用者名・表示名・有効状態の 3 つだけ**であり、ロール・ABAC 属性・内部 ID を運ぶ項目を持たない
// （面に出す項目を型で閉じる。`UserDirectoryGrpcService` が s2s の面で採った作法と同じ）。
// `Username` が共有台帳の `subjectId`（`${current_user}` と同じ名前空間。ADR-0098 決定 1）である。
// 画面には `DisplayName` だけを出す。

// SC-19 主要素 3: 利用者の像（検索結果・共有先表示の両方が使う）。
public record UserSummaryDto(string Username, string DisplayName, bool Enabled);

// SC-19 主要素 3: 表示名へ引く利用者名の集合（1〜100 件）。居ない名前は応答から落ちる。
public record ResolveUsersRequest(List<string> Usernames);
