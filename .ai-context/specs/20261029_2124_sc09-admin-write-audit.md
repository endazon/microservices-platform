---
title: 作業仕様書 — SC-09 の ABAC 設定の書き込み操作を監査ログに記録する（#2124）
type: spec
status: done
related_ids: [FR-09, FR-17, SC-09, UC-05, ADR-0004, NFR-28, IADR-0040, IADR-0044, IADR-0516, IADR-0647]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2124"
---

# 作業仕様書 — SC-09 の書き込み操作の監査（#2124）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `bd3b47d8`）。親は #2047（指摘 M-24 の SC-09 監査ログ）。

## 現状（実測）

- 計画 SC-09 §アクセス制御は「システム管理者ロール限定。**操作は監査ログに記録する**」と定める。
- SC-09 の書き込みは BFF の 3 群を通る。どこにも `IAuditLogger` の呼び出しが無い（`git grep IAuditLogger` で 0 件）。
  - `/bff/admin/authz/*`（`Platform.Bff/Foundation/Endpoints/AuthzBffEndpoints.cs`。後段は AuthorizationService）。
  - `/bff/tags/*`（`Knowledge.Bff.Endpoints/TagDictionaryBffEndpoints.cs`。後段は DocumentService）。
  - `/bff/edge-types/*`（`Knowledge.Bff.Endpoints/EdgeTypeDictionaryBffEndpoints.cs`。後段は GraphService）。
- 後段の 3 サービス（AuthorizationService・DocumentService のタグ辞書・GraphService の辺の型）にも監査の記録は無い。AuthorizationService には `IAuditLogger` の登録自体が無い。
- 同じ BFF の管理面（SC-22 の構成情報・秘密項目）は BFF で `IAuditLogger` に記録している。SC-12 は McpServer の `McpClientAudit` が記録する。

## 母集合（規則 9。SC-09 の画面が呼ぶ書き込みの口を、画面の生成フックから引いた）

`grep -rn "useBff" src/knowledge/frontend/src/features/sc09-admin-abac` の書き込みフック → BFF の端点名で突き合わせた。

| 口 | 操作 | 監査の action | 対象 |
| --- | --- | --- | --- |
| `POST /bff/admin/authz/policies` | ポリシーの作成 | `abac.policy.create` | 応答の `id` |
| `PUT /bff/admin/authz/policies/{id}` | ポリシーの更新 | `abac.policy.update` | `{id}` |
| `PATCH /bff/admin/authz/policies/{id}/active` | 有効／無効の切替 | `abac.policy.set-active` | `{id}` |
| `DELETE /bff/admin/authz/policies/{id}` | ポリシーの削除 | `abac.policy.delete` | `{id}` |
| `POST /bff/admin/authz/attributes` | 属性の作成 | `abac.attribute.create` | 応答の `id` |
| `PUT /bff/admin/authz/attributes/{id}` | 属性の更新 | `abac.attribute.update` | `{id}` |
| `DELETE /bff/admin/authz/attributes/{id}` | 属性の削除 | `abac.attribute.delete` | `{id}` |
| `POST /bff/tags` | タグの追加 | `tag.create` | 応答の `id` |
| `PUT /bff/tags/{id}` | タグの改名 | `tag.rename` | `{id}` |
| `DELETE /bff/tags/{id}` | タグの削除 | `tag.delete` | `{id}` |
| `POST /bff/edge-types` | 辺の型の追加 | `edge-type.create` | 応答の `id` |
| `PUT /bff/edge-types/{id}` | 辺の型の改名 | `edge-type.rename` | `{id}` |
| `DELETE /bff/edge-types/{id}` | 辺の型の削除 | `edge-type.delete` | `{id}` |

除外:

- `POST /bff/admin/authz/policies/validate`（dry-run。保存しない＝書き込みではない）と、各群の GET（読み取り）。
- issue の「ロール・属性の割当」: 利用者へのロール・属性の割当は SC-17（`UserAdminBffEndpoints.cs`）の操作で、SC-09 には無い。本件の射程外として報告に残す（SC-17 も記録 0 件である）。
- BFF の `AdminOnly` で弾かれた要求（403）は端点に届かない。SC-12 の `McpClientAudit` と同じく、ここでは記録しない（認可の拒否は認証基盤側の記録に残る）。

## 方針（判断は IADR-0647）

- **記録は BFF で行う**（SC-22 と同じ器・同じ層）。SC-09 の 3 群とも BFF が唯一の入口であり、後段 3 サービスは east-west の内部 API（エッジに出さない）である。
- 1 操作 1 行。`subject` は利用者名（`preferred_username`）、`outcome` は後段の状態コードから決める（2xx → `granted`、400 / 401 / 403 / 409 → `denied`、404 → `not-found`、それ以外と不達 → `failed`）。
  - 拒否・不在・失敗も残す（成功だけを残すと「試みた」が追えない）。
- `detail` は `target=<id> status=<code>` だけにする。**名前・条件・説明などの利用者入力は載せない**（NFR-28。`AuditLogger` は無害化するが、そもそも監査に要らない値を載せない）。作成で ID が応答から読めないとき（4xx など）は `target=-`。
- 書き方は 1 か所（`Platform.Shared.Infrastructure.Foundation.Audit.AdminWriteAudit`）に置き、platform・knowledge の両 BFF 群から使う（knowledge は platform の Shared だけを参照してよい）。

## 受け入れ基準

- [x] SC-09 の書き込み 13 口のそれぞれで、監査ログが 1 件だけ出る（action・利用者名・`granted`・対象 ID）。修正前は 0 件で赤
- [x] 後段が 409 / 400 を返したときは `denied`、到達できないときは `failed` で 1 件残る
- [x] 検証（validate）と読み取り（GET）は記録しない
- [x] `detail` に利用者の入力（名前など）を載せない

## 規則 10（この変更で新たに誤りになる自分の記述）

- `AuthzBffEndpoints.cs` の冒頭コメント（中継するだけ）→ 監査を記録することを足す。
- `docs/screens/SC-09_admin-abac-settings.md` に監査の記述があれば追随する（走査: `grep -n "監査" docs/screens/SC-09_admin-abac-settings.md`）。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
