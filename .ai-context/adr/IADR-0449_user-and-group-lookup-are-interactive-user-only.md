---
title: IADR-0449 利用者・グループの名簿の読み口（`/authz/users/*`・`/authz/groups/*`・BFF の `/bff/users/*`・`/bff/groups/*`）は「人の主体だけ」の認可ポリシー `InteractiveUser` で守る — ロールではなく主体の種別で分ける
type: impl-adr
status: Accepted
related_ids: [FR-19, SC-19, ADR-0098, ADR-0100, IADR-0401, IADR-0445, IADR-0447, IADR-0449]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0100_user-lookup-reach-is-all-users.md
related_specs:
  - ../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md
---

# IADR-0449: 名簿の読み口は「人の主体だけ」の認可ポリシーで守る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-12
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-19・SC-19（主要素 3）／ADR-0100（決定 1 到達範囲は全利用者・決定 2 面は 3 項目・**フォローアップ 2** サービスアカウントの経路を分ける）／ADR-0098（決定 1）
- 関連 issue: planning#621（環流。ADR-0100 の起点）、#1447
- 関連 IADR: IADR-0401（名簿の列挙をサービス間の面へ出さない。**前提として据え置く**）・IADR-0445（利用者検索の面。`RequireAuthorization()` のみだった）・IADR-0447（グループ検索の面）
- 作業仕様書: `../specs/20260912_1447-1448_current-groups-binding-and-set-valued-matching.md`

## コンテキストと課題

ADR-0100 は「本 ADR が開いたのは人が操作する面であり、サービス間の面ではない。サービスアカウントがこの口を使う経路は本 ADR の対象ではない」とし、フォローアップ 2 で**実装側で経路を分ける**ことを求めた。実測（2026-09-12）:

- `/authz/users/lookup`・`/resolve`（IADR-0445）は `RequireAuthorization()` のみ。realm のサービスアカウント（`service-account-*`。21 主体。全員 `platform-service`）は認証済みなので**到達できる**。
- 既存の認可ポリシーは `AdminOnly` / `ConfigViewer` / `ServiceCaller` の 3 つで、**「人だけ」を表すものは無い**。`AdminOnly` も人限定ではない（`service-account-abac-seeder` は `platform-admin` を持つ）。
- 主体の種別を判定する部品は既にある: `MachinePrincipal.IsMachine`（腕 A `preferred_username` が `service-account-` で始まる／腕 B 利用者名が無く `azp`・`client_id` がある。BFF の Bearer 受理 `BearerCallerPolicy` が同じ判定を使う）。

## 検討した選択肢

| 案 | 判定 |
| --- | --- |
| ロールで分ける（`platform-service` を持つ主体を拒む） | 採らない。ロールは付け替えられる。「人か機械か」は主体の種別であってロールではない |
| 各端点のハンドラで `IsMachine` を見て 403 | 採らない。面が増えるたびに写す。認可はポリシーとして端点の外に置く（`AdminOnly` 等と同じ作法） |
| **認可ポリシー `InteractiveUser`（認証済み ∧ ¬`IsMachine`）を新設し、名簿の読み口の群に課す** | **採る** |
| BFF だけで止める | 採らない。実施点は後段（BFF を経由しない s2s 呼び出しが問題そのものである）。BFF にも課すのは多層防御 |

## 決定

1. **`PlatformAuthPolicies.InteractiveUser`** を `AuthExtensions` に登録する: `RequireAuthenticatedUser()` ∧ `RequireAssertion(ctx => !MachinePrincipal.IsMachine(ctx.User))`。判定の部品は `MachinePrincipal` を再利用し、**「機械」の定義を 2 か所に持たない**。
2. **`/authz/users/lookup`・`/authz/users/resolve`・`/authz/groups/lookup`・`/authz/groups/resolve` の群に課す**（実施点）。**BFF の `/bff/users/*`・`/bff/groups/*` にも課す**（多層防御）。`openapi.yaml` の `x-roles: []` は変えない（ロールの要求ではないため）。
3. サービスアカウントは `platform-admin` を持っていても 403。人は従前どおり。未認証は 401。陽性・陰性対照をテストで固定する。
4. `AdminOnly` の `/authz/users`（全件列挙）と s2s の `UserDirectory` gRPC（`ServiceCaller`）は変えない。**IADR-0401 の分界（列挙をサービス間の面へ出さない）は本 IADR によって保たれる** —— 新しい読み口はサービスから使えない。

## 結果

- **良い影響**: ADR-0100 が開いた面が「人が操作する面」に閉じる。「人だけ」のポリシーが 1 つできたので、次に同種の面が増えても写さずに済む。
- **悪い影響 / 残余リスク**: `MachinePrincipal.IsMachine` の腕 A（利用者名の接頭辞）は Keycloak の命名規則に依存する。命名規則が変われば人と見なされる —— 腕 B（利用者名が無い）が二重に守るが、利用者名を持つサービスアカウントを別の接頭辞で作られると通る。`BearerCallerPolicy` と同じ受容済みのリスクである。
- MCP サーバーの有人エージェント（利用者の代理。`McpSubject.IsServiceAccount=false`）が BFF を経由して名簿を引く経路は、利用者のトークンで到達するため人と同じ扱いになる。ADR-0100 決定 1 の「認証済みの利用者」の範囲内である。
