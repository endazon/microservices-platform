namespace Platform.Shared.Contracts.Dtos;

// FR-19, SC-19 主要素 3, ADR-0098 決定 1・3, ADR-0100 フォローアップ 2, #1447: 共有先のグループ指定のための
// グループ検索の DTO（BFF ↔ SPA 契約）。AuthorizationService の `/authz/groups/lookup`・`/authz/groups/resolve`
// （認証必須・ロール不問・**人の主体だけ**）と JSON 互換であり、BFF は利用者の `Authorization` を転送して透過中継する。
//
// 🔴 **面は識別子・表示名・パスの 3 つに閉じる。** 所属者・属性・ロールを運ぶ項目を持たない
// （`UserSummaryDto` が利用者の面を 3 項目に閉じたのと同じ作法。ADR-0100 決定 2 の向き）。
// `Id` が共有台帳の `subjectId`（`${current_groups}` と同じ名前空間 ＝ Keycloak のグループ ID。ADR-0098 決定 1）である。
// 画面には `DisplayName` を出し、同名グループの区別に `Path` を添える。**`Id` は画面に出さない。**
//
// 🔴 **なぜグループ ID であって名前・パスではないのか**: 改名・移動で共有が黙って外れないためである。
// `${current_groups}` の供給元（IdP の所属照会）も同じ ID を運ぶので、突合の両側が同じ値で揃う。

// SC-19 主要素 3: グループの像（検索結果・共有先表示の両方が使う）。
public record GroupSummaryDto(string Id, string DisplayName, string Path);

// SC-19 主要素 3: 表示名へ引くグループ ID の集合（1〜100 件）。無い ID は応答から落ちる。
public record ResolveGroupsRequest(List<string> Ids);
