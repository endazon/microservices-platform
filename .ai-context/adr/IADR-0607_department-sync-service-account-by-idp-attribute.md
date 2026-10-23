---
title: IADR-0607 部門の同期はサービスアカウントを IdP が返す属性（serviceAccountClientId）だけで見分け、無人主体の腕 B の前提は既存の realm 検査と作成後の読み戻しで守る（新しい検査は足さない）
type: impl-adr
status: Accepted
related_ids: [NFR-09, FR-05, FR-09, SC-17, SC-12, ADR-0032, ADR-0115, ADR-0116, IADR-0606, IADR-0473, IADR-0420, IADR-0516]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-09）
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md
related_specs:
  - ../specs/20261023_ghsa-94cm_department-sync-service-account-and-arm-b.md
---

# IADR-0607: 部門の同期のサービスアカウント判定を IdP の属性に寄せ、腕 B の前提は既存の検査で守る（GHSA-94cm-55g9-2967 の追補）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（GHSA-94cm-55g9-2967 の追補。多層防御の強化）

## 起点・関連

- 関連する計画書 ID: NFR-09、FR-05 / FR-09 / SC-17（利用者属性 `department` の同期）、ADR-0115 決定 3・ADR-0116 決定 2。
- 前提とする実装 ADR: [IADR-0606](./IADR-0606_machine-principal-issuing-client-match.md)（無人主体の判定を発行元のクライアント識別に基づかせる）、
  [IADR-0473](./IADR-0473_department-attribute-follows-department-group.md)（部門の同期）、
  [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)、
  [IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md)（SC-12 の MCP クライアントの作成）。
- 作業仕様書: [20261023_ghsa-94cm_department-sync-service-account-and-arm-b](../specs/20261023_ghsa-94cm_department-sync-service-account-and-arm-b.md)。

## コンテキストと課題

IADR-0606 は無人主体の判定の根拠を、IdP が書き利用者が変えられない属性へ寄せた。同じ advisory の範囲で 2 点が残っていた。

1. 部門の同期（IADR-0473）は、全利用者の列挙から機械の主体を除くとき、Keycloak の利用者表現の `serviceAccountClientId` に加えて
   **利用者名の接頭辞（`service-account-`）でも除いていた**。利用者名は realm の利用者管理で付ける値である。判定の根拠を IdP の属性だけに揃え、
   迷ったら同期の対象に残す側（属性を部門グループへ合わせる側）へ倒す。
2. IADR-0606 は腕 B（利用者名が無い主体）を変えていない。腕 B の前提は「人がログインするクライアントのトークンには、必ず利用者名
   （`preferred_username`）が載る」ことである。この前提が機械的に守られているかを確かめ、検査を足すか文書で受容するかを決める必要があった。

## 決定

### 決定 1: 部門の同期はサービスアカウントを IdP の属性だけで見分ける

- `IdentityUser` に任意の `ServiceAccountClientId` を足す。Keycloak の実装は利用者表現の `serviceAccountClientId` をそのまま写す（空は null）。
  利用者名から推定しない。API の応答（DTO）には写さない。
- `DepartmentAttributeReconciliation.IsServiceAccount` は利用者の像を受け取り、`ServiceAccountClientId` を持つときだけ真を返す。
  利用者名の接頭辞の定数と、名前で判定する多重定義は消す。
- `KeycloakIdentityAdminClient.ListAllUsersAsync`・偽物の `InMemoryIdentityAdminClient`・呼び出し元の `DepartmentAttributeSync`
  （除き損ねたときの二重の歯止め）は、すべて同じ判定を使う。
- 利用者名が接頭辞の形でも `serviceAccountClientId` を持たない利用者は人として同期の対象に残す（部門グループに属さなければ属性を消す）。

### 決定 2: より厳しい側に倒すことの影響を受容する

IdP が `serviceAccountClientId` を返さない形で列挙を読んだ場合、サービスアカウントも人として扱われ、部門グループに属さなければ属性 `department` が消える。
これは資料が見えなくなる側（fail-closed）の誤りであり、受容する。Keycloak は `briefRepresentation=false` の利用者一覧で
サービスアカウントの利用者に `serviceAccountClientId` を返す（既存の試験が応答の形を固定している）。同期は既定 `Off` で、`Fix` の前に `Report` で対象を確かめる運用（運用仕様書）は変えない。

### 決定 3: 腕 B の前提には新しい検査を足さない（既存の検査と作成後の読み戻しで足りる）

実測（作業仕様書 §腕 B の前提の確認）:

- realm の宣言: `scripts/check-realm-constraints.js` 検査 7 の (2)・(2')・(2'') が、人がログインする経路（標準フロー〔未設定の既定 true を含む〕・暗黙・直接アクセス・
  デバイスグラント・CIBA）を開いた**すべての**クライアントについて、`profile` が既定スコープにあること、`profile` スコープが利用者名から `preferred_username` を
  載せること、軽量アクセストークンでも落ちないことを止めている。実データの realm の該当 8 件はすべて通り（陽性対照）、
  8 件それぞれから `profile` を外す変異・サービスアカウント専用のクライアントでログインの経路を開く変異・スコープから写像を外す変異はすべて名指しされる。
  サービスアカウント専用のクライアントは名指しされない（陰性対照）。自己試験 207 件も緑。
- 宣言の外で作られる人のログインのクライアント（SC-12 の有人 MCP クライアント）: McpServer が作成後に表現を読み戻し、
  `defaultClientScopes` に `profile` が無ければ違反として止める（`KeycloakServiceAccountProvisioner.PublicClientViolations`。IADR-0516）。

運用規約は「検査器・規約の追加は同型の事故 2 回から」とする。advisory の多層防御としては足す理由になり得るが、
今回は守るべき前提がすでに 2 か所（宣言の検査・作成時の読み戻し）で機械的に守られており、足すと同じ規則を 2 か所に持つことになる。
したがって足さず、検査 7 の注記に「腕 B の前提をどこが守るか」を書き足すだけにする（IADR-0606 で腕 A の説明が古くなった箇所も追随した）。

## 検討した代替案

| 案 | 採否 | 理由 |
| --- | --- | --- |
| 接頭辞の判定を残し、`serviceAccountClientId` との OR にする（従前） | 不採用 | 利用者名の形だけで同期の対象から外れる余地が残る |
| 接頭辞と `serviceAccountClientId` の AND にする | 不採用 | Keycloak の命名規則への依存を増やすだけで、判定は `serviceAccountClientId` 単独と変わらない（正規の SA は両方を持つ） |
| 部門の同期を利用者ごとの個別取得（`/users/{id}`）で確かめ直す | 不採用 | 一覧の表現が同じ属性を返すので往復を増やす理由が無い |
| 腕 B の前提を確かめる検査を新設する | 不採用 | 検査 7 (2) と同じ規則の重複になる（決定 3） |
| realm の宣言型ユーザープロファイルで利用者名のパターンを禁じる | 本 IADR では採らない | IADR-0606 と同じ（稼働の realm の再適用が要る） |

## 結果

- 良い影響: 部門の同期が機械の主体を見分ける根拠が、無人主体の判定（IADR-0606）と同じく IdP の属性に揃う。正規のサービスアカウントは従前どおり同期から外れる（試験で固定）。
- 悪い影響 / 残余リスク:
  1. IdP が `serviceAccountClientId` を返さなくなる版の変化があると、サービスアカウントの属性 `department` が消え得る（fail-closed。決定 2）。Keycloak を上げるときは同期を `Report` で確かめる。
  2. MCP の実行で個人資料を落とす判定（DocumentService / GraphService / RetrievalService の `IsServiceAccount`）は利用者名の接頭辞のままである。
     こちらは真のとき資料を減らす側（厳しい側）に働くので本 IADR の射程に含めない。
  3. 腕 B の前提は宣言と SC-12 の作成時に守られる。稼働中の realm で管理コンソールから足したクライアントは宣言の外であり、運用の注意書き（SC-17 画面仕様書）に留まる（IADR-0606 と同じ）。
