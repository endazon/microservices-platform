---
title: IADR-0647 SC-09 の書き込み操作の監査は BFF で 1 操作 1 行に記録し、利用者の入力値は載せない
type: impl-adr
status: Accepted
related_ids: [FR-09, FR-17, SC-09, UC-05, ADR-0004, NFR-28, IADR-0040, IADR-0044, IADR-0516]
author: claude
created: 2026-10-29
updated: 2026-10-29
related_specs:
  - ../specs/20261029_2124_sc09-admin-write-audit.md
---

# IADR-0647: SC-09 の書き込み操作の監査は BFF で記録する（#2124）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-29
- 決定者: claude（#2124。親 #2047 の指摘 M-24）

## 起点・関連

- 起点 issue: #2124。計画 SC-09 §アクセス制御「操作は監査ログに記録する」。
- 前提として変えない: [IADR-0040](./IADR-0040_admin-abac-bff-passthrough-and-admin-only.md)（BFF は透過中継し、判断は後段に置く）・IADR-0044（BFF と後段の両方で AdminOnly）。
- 同型の先例: SC-22（構成情報・秘密項目。BFF で `IAuditLogger`）、SC-12（[IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md)。McpServer の `McpClientAudit`）。
- 作業仕様書: [20261029_2124_sc09-admin-write-audit](../specs/20261029_2124_sc09-admin-write-audit.md)。

## 背景

- SC-09 の書き込みは BFF の 3 群（`/bff/admin/authz`・`/bff/tags`・`/bff/edge-types`）を通り、後段は 3 サービス（AuthorizationService・DocumentService・GraphService）である。
- BFF にも後段にも記録が無かった。AuthorizationService には `IAuditLogger` の登録自体が無い。

## 決定

### 決定 1: 記録する層は BFF とする

- SC-09 の書き込みの入口は BFF だけである。後段 3 サービスは east-west の内部 API で、エッジに出さない。
- 同じ BFF の管理面（SC-22）が既に BFF で記録しており、器（`IAuditLogger` → `Audit=true` の構造化ログ → OTel）も同じである。
- **採らなかった案: 後段 3 サービスで記録する。** 迂回（BFF を通らない呼び出し）まで覆える。ただし記録の書き方が 3 サービスに割れ、AuthorizationService には器の登録から足すことになる。後段が内部 API のままである限り、迂回の経路は運用者が後段へ直接届く場合に限られる。この残余は「残余リスク」に置く。

### 決定 2: 1 操作 1 行。結果は後段の状態コードから決める

- action は `abac.policy.{create,update,set-active,delete}`・`abac.attribute.{create,update,delete}`・`tag.{create,rename,delete}`・`edge-type.{create,rename,delete}` の 13 種とする。
- subject は利用者名（`preferred_username`）である。名前が無いときは `unknown` と残す（記録を落とさない）。
- outcome は、2xx → `granted`、400 / 401 / 403 / 409 → `denied`、404 → `not-found`、それ以外と後段不達 → `failed` とする。拒否・失敗も残す。
  - 後段の 401 / 403 は「試みたが拒否された」なので `denied` に寄せる。BFF 段の拒否は端点に届かないので、ここに出る 401 / 403 は後段のものである。区別は detail の `status=` で読む。
- **検証（`/policies/validate`）と読み取りは記録しない**（保存しないため）。BFF の AdminOnly で弾かれた要求は端点に届かないので記録しない（SC-12 と同じ）。

### 決定 3: detail は対象 ID と状態コードだけ。利用者の入力値は載せない

- detail は `target=<id> status=<code>` とする。作成の対象 ID は後段の応答の `id` から読み、読めなければ `target=-` にする。
- 名前・条件・説明は載せない（NFR-28）。`AuditLogger` は無害化するが、監査に要らない外部由来の文字列はそもそも載せない。

### 決定 4: 書き方は 1 か所に置く

- `Platform.Shared.Infrastructure.Foundation.Audit.AdminWriteAudit` に置き、platform の BFF 群と knowledge の BFF 群の両方から使う。knowledge が参照してよいのは platform の Shared だけなので、置き場所はここに決まる。
- SC-12 の `McpClientAudit` は本 IADR では寄せない（射程外。outcome の写像は同じ形にそろえた）。

## 残余リスク

- BFF を通らずに後段 3 サービスを直接呼ぶ要求は記録されない。後段は AdminOnly で守られ、エッジにも出ていない。後段へ直接届く呼び出しが現れたら、決定 1 を見直して後段でも記録する。
- クライアントが要求の途中で切断した（`OperationCanceledException`）ときは監査を残さない。後段の書き込みが成功していても 0 行になり得る。
- 利用者へのロール・属性の割当（SC-17 `UserAdminBffEndpoints`）にも記録は無い。SC-09 の操作ではないので本件の射程外とした（#2298 で扱う）。
