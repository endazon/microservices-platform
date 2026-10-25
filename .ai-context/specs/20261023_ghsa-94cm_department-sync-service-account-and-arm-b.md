---
title: 作業仕様書 — 部門の同期のサービスアカウント判定を IdP の属性に寄せ、無人主体の腕 B の前提を確かめる（GHSA-94cm-55g9-2967 の追補）
type: spec
status: done
related_ids: [NFR-09, FR-05, FR-09, SC-17, ADR-0032, ADR-0115, ADR-0116, IADR-0607, IADR-0606, IADR-0473, IADR-0420]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md
issue: "GHSA-94cm-55g9-2967"
---

# 作業仕様書 — 部門の同期のサービスアカウント判定と、腕 B の前提の確認

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0607**（新規）に置く。
> IADR-0606（無人主体の判定。develop にマージ済み）の追補であり、多層防御の強化である。参照は GHSA の ID だけとする。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（OIDC/JWT 認証）、**FR-05 / FR-09 / SC-17**（利用者属性 `department` の同期）。
- 計画 ADR: **ADR-0115 決定 3**（部門の正本は部門グループ）・**ADR-0116 決定 2**（部門グループに属さない人の属性は消す）。
  矛盾しない —— 計画は「同期から外す機械の主体」をどの属性で見分けるかを定めていない（実装裁量。IADR-0473）。本件はその判定を IdP の属性へ寄せる。
- 実装 ADR: **IADR-0607**（新設）。前提: IADR-0606（無人主体の判定）・IADR-0473（部門の同期）・IADR-0420。

## 要件（コーディネーター指示 2026-10-23）

1. 部門の同期（`DepartmentAttributeReconciliation.IsServiceAccount` と `KeycloakIdentityAdminClient.ListAllUsersAsync`）が
   サービスアカウントを見分けるとき、利用者名の接頭辞ではなく、Keycloak の管理 API が返す権威のある属性（利用者表現の `serviceAccountClientId`）で判定する。
   接頭辞だけの判定は無くし、より厳しい側（人として同期の対象に残す側）へ倒す。
2. 接頭辞つきの名前を持つ人の利用者が同期の対象に残ることを試験で固定する。
3. 無人主体の腕 B の前提（人がログインするクライアントのトークンには利用者名が載る）を realm の宣言で確かめ、
   検査を足すか文書で受容するかを根拠つきで IADR に記録する。

## 母集合（規則 9: 誤りの側の文字列で走査してから挙げる）

`git grep -n "IsServiceAccount\|ServiceAccountUsernamePrefix" -- src` と
`git grep -n "service-account-\` で始まる" -- docs scripts`（部門の同期・無人主体の判定の記述）で引いた。

| 箇所 | 種別 | 対応 |
| --- | --- | --- |
| `AuthorizationService/Domain/DepartmentAttributeReconciliation.cs`（`IsServiceAccount(string)`・`ServiceAccountUsernamePrefix`） | 判定 | 利用者の像の `ServiceAccountClientId` で判定する形へ置き換え、接頭辞の定数を消す |
| `AuthorizationService/Infrastructure/ExternalServices/KeycloakIdentityAdminClient.cs`（`ListAllUsersAsync`・`ToIdentityUser`） | 判定・写像 | `serviceAccountClientId` だけで除き、像へ写す |
| `AuthorizationService/Infrastructure/ExternalServices/InMemoryIdentityAdminClient.cs`（`ListAllUsersAsync`） | 偽物 | 本物と同じ規則 |
| `AuthorizationService/Features/Users/DepartmentSync/DepartmentAttributeSync.cs` | 呼び出し元 | 像の属性で判定（二重の歯止めは残す） |
| `AuthorizationService/Domain/Ports/IIdentityAdminClient.cs`（`IdentityUser`） | 型 | 末尾に任意の `ServiceAccountClientId` を足す（API の応答には写さない） |
| 試験 3 本（`DepartmentAttributeReconciliationTests`・`KeycloakIdentityAdminClientTests`・`DepartmentAttributeSyncTests`） | 試験 | 下記 |
| `docs/operations/operations.md`（部門の同期の節）・`docs/tests/SC-17_user-account-management.md` T-58 | 文書 | 判定の根拠を「利用者名の接頭辞」から「IdP がサービスアカウントとして返す利用者」へ直す |
| `scripts/check-realm-constraints.js` 検査 7 の注記（腕 A の説明） | 注記 | IADR-0606 で腕 A が一致判定になったので説明を追随する（規則 10） |

除外: `docs/tests/FR-16_mcp-server.md` X-20 / X-44 / X-61・`docs/api/east-west-grpc.md` の個人資料の除外は別の判定（MCP の実行主体）であり本件の射程外。
`docs/screens/SC-17` §運用上の注意は realm の宣言の検査の説明で、腕 A の手掛かりとして接頭辞を挙げる記述は IADR-0606 後も正しい。

## 腕 B の前提の確認（実測）

`node scripts/check-realm-constraints.js` の検査 7 (2)・(2')・(2'') が既にこの前提を宣言で守っている。実データの realm で次を測った。

- 人がログインするクライアント（`isHumanLoginClient`）: `wiki-js` / `bff` / `headlamp` / `grafana` / `argocd` / `vault` / `account` / `account-console` の 8 件。全件が `profile` を既定スコープに持つ（陽性対照: 違反 0 件）。
- 変異（一時オブジェクト）: 8 件それぞれで `profile` を既定から外し任意スコープへ移す → 各 1 件を名指し（8/8 赤）。
  サービスアカウント専用のクライアントの `standardFlowEnabled` を未設定にする（既定 true）→ 名指し。デバイスグラントを開く → 名指し。
  `profile` スコープから利用者名の写像を外す → 名指し。
- 陰性対照: サービスアカウント専用のクライアント（標準フローを閉じたもの）は名指ししない（実データで違反 0 件）。
- 宣言の外で作られる人のログインのクライアント（SC-12 の有人 MCP クライアント）は、作成後の読み戻し（`KeycloakServiceAccountProvisioner.PublicClientViolations`）が
  `defaultClientScopes` に `profile` が無いことを違反として止める（既存）。

→ **新しい検査は足さない**（IADR-0607 決定 3）。

## 試験（受け入れ基準の写像）

| ID | 内容 | 期待 |
| --- | --- | --- |
| A-1 | `IsServiceAccount`: `ServiceAccountClientId` を持つ像 / 接頭辞の名前だけを持つ像 / 名前も属性も無い像 | 真 / 偽 / 偽 |
| A-2 | Keycloak の全利用者の列挙: `serviceAccountClientId` を持つ利用者（名前の形によらない）は返さず、接頭辞の名前だけの人は返す。像に `ServiceAccountClientId` を写す | 人は残る・SA は除く |
| A-3 | 同期（`Fix`）: 部門グループに属さず `department` を持つ、接頭辞の名前の人 → 属性を消す。正規の SA（`serviceAccountClientId` あり）→ 消さない | 人は消える・SA は残る |
| A-4 | 変異: 接頭辞の判定を戻す → A-1・A-2・A-3 が赤。SA の除外を外す → 既存 T-58 が赤 | 赤 |

## 完了条件

- `dotnet build` / `dotnet test`（AuthorizationService）緑、`dotnet format --verify-no-changes`。
- `node scripts/check-realm-constraints.js --self-test` と本走査、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、文書系検査が緑。
