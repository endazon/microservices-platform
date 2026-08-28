---
title: ワンタイムコード（OTP／多要素認証） テスト仕様書
type: test-spec
status: in-progress
created: 2026-08-15
updated: 2026-08-28
author: claude
---
<!-- trace:
ids: [SC-13, SC-14, SC-16, UC-05]
adrs: [ADR-0026]
iadrs: [IADR-0197, IADR-0294]
specs: []
issues: [#438]
-->

# テスト仕様書: ワンタイムコード（OTP／多要素認証）

## 起点となる計画書（トレーサビリティ）

- 画面: ワンタイムコード（OTP）
- ユースケース: ABAC 権限を管理する
- 受け入れ基準の所在: 認証 UX とアカウント管理の計画 ADR §多要素認証／計画側の画面設計 §ワンタイムコード（OTP）

## テスト対象・範囲

**対象**: `deploy/keycloak/microservices-platform-realm.json` が認証 UX の計画 ADR が定める OTP 要件を満たすこと（**静的検査**）。

**対象外（実環境が要る）**: 実際の TOTP 検証・初回セットアップ画面・リカバリーコード表示。
**Keycloak を起動しないと検証できない**（CI は Keycloak を起動しない。実測: `.github/workflows/` に Keycloak を
起動するジョブは無く、`ci.yml` の `realm-constraints` ジョブ〔343〜353 行〕は静的検査のみ）。**これらは #438 の射程である。**

## テスト観点

- **realm 設定が確定要件と一致すること**（値の一致。自動）
- **必須アクションの `defaultAction` が真であること** —— `enabled` だけでは未登録者は誘導されない（境界）
- **TOTP の許容ずれが「前後 1 ステップ」であること** —— `otpPolicyLookAheadWindow` は Keycloak では `[-n, +n]` の対称窓である（境界）

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | realm export が存在する | `node scripts/check-realm-constraints.js` | `otpPolicyType = "totp"` | 計画 ADR「TOTP による MFA を必須とする」 | 自動 |
| T-02 | 同上 | 同上 | `otpPolicyDigits = 6` | 計画 ADR「6桁」 | 自動 |
| T-03 | 同上 | 同上 | `otpPolicyPeriod = 30` かつ `otpPolicyLookAheadWindow = 1` | 計画 ADR「時刻ずれは前後1ステップ〔30秒〕まで許容」 | 自動 |
| T-04 | 同上 | 同上 | `requiredActions` に `CONFIGURE_TOTP` があり **`enabled: true` かつ `defaultAction: true`** | 計画 ADR「未登録者はログイン時に初回セットアップへ誘導する」 | 自動 |
| T-05 | 同上 | 同上 | `otpPolicyCodeReusable = false` | 同一コードの再利用を許さない（TOTP の前提） | 自動 |
| T-10 | 同上 | 同上（検査 5） | **対話ログインする利用者が全員** `requiredActions` に `CONFIGURE_TOTP` を持つ | 🔴 **T-04 だけでは足りない。** provider 側の `defaultAction` は**新規に作られる利用者にしか付かず**、realm import で作られる利用者には遡及しない。T-04 が緑でも**未登録者がパスワードだけで通る**状態が成立し得た（実測） | 自動 |
| T-11 | 同上 | 同上（検査 5） | **サービスアカウント**（`serviceAccountClientId` を持つ利用者）には `CONFIGURE_TOTP` が**付いていない** | 陽性対照。付けるとトークン取得が `Account is not fully set up` で壊れる。T-10 を「全利用者へ付ける」と読み違えたときに落ちる | 自動 |
| T-12 | 同上 | 同上（検査 5） | realm の**全 client** で `directAccessGrantsEnabled` が `true` でない | パスワードグラントは browser フローを通らないため、開いていると **OTP を一切問われずにトークンが出る**（MFA のバイパス口） | 自動 |
| T-13 | 同上 | 同上（検査 5） | `requiredActions` の `delete_credential` が `enabled: false` | 登録済み OTP を利用者自身が消せると、強制登録を通った後に MFA 無しの状態へ戻れる（再発行は管理者側の画面が担う） | 自動 |
| T-14 | 同上 | 同上（検査 5） | `eventsEnabled` / `adminEventsEnabled` / `adminEventsDetailsEnabled` が `true`、`eventsListeners` に `jboss-logging`、`enabledEventTypes` に最小集合（`LOGIN` / `LOGIN_ERROR` / `LOGOUT` / `UPDATE_PASSWORD` / `UPDATE_TOTP` / `REMOVE_TOTP` / `RESET_PASSWORD` / `REMOVE_CREDENTIAL`） | 計画 ADR「操作を監査ログに記録する」／メール停止時の代替手順が「申請者・承認者・実行者を残す」ことを前提にしている。Keycloak の既定は「記録しない」であり、**書かなければ 1 件も残らない** | 自動 |
| T-15 | — | `node scripts/check-realm-constraints.js --self-test` | 検査 5 の不変条件を **1 つずつ壊した変異 9 件がそれぞれ検出され**、陽性対照（サービスアカウント）は検出されない | 🔴 **正例だけでは検出力を測れない。** 「実データで緑」は検査が効いていることの証拠にならない | 自動 |
| T-06 | Keycloak 稼働・TOTP 未登録の利用者 | ログイン画面からログインする | `CONFIGURE_TOTP` の初回セットアップへ誘導される | 計画 ADR「未登録者は…誘導する」 | **手動（実環境）** |
| T-07 | Keycloak 稼働・TOTP 登録済 | 1 ステップ前／後のコードを入力する | いずれも受理される | 計画 ADR「前後1ステップまで許容」 | **手動（実環境）** |
| T-08 | 同上 | 2 ステップ前のコードを入力する | 拒否される | 同上（**境界の外**） | **手動（実環境）** |
| T-09 | 初回登録を完了する | 登録完了画面を確認する | リカバリーコードが **1 回のみ**表示される | 計画側の画面設計 §ワンタイムコード（OTP） | **手動（実環境）** |

## テストデータ

`deploy/keycloak/microservices-platform-realm.json`（リポジトリ内。追加のテストデータは要らない）。

## 関連仕様

- 画面仕様書: [ワンタイムコード（OTP）](../screens/SC-14_otp-mfa.md)
- 実装 ADR: レルムを `platform` へ改名し、計画 ADR の認証ポリシーを realm へ投入する
- 実装 ADR: MFA を「必須アクション＋直接付与の閉鎖」で実効化し、認証フローは宣言しない

## 未決事項

- **T-06〜T-09 は実環境が要る。** 自動側（T-01〜T-05・T-10〜T-15）は realm の**宣言**を測るものであり、
  **実際にログイン画面で第二要素が要求されることは測っていない**。
  🔴 「統制を定めた」と「統制が働いている」は別であり、ここで測れるのは前者だけである。
- **T-06 の自動化の目処は立っている。** 検証器（`scripts/verify-oidc-edge-flow.sh`）が OTP の段を
  自分で通せるようになった（`scripts/lib/totp.js`）。ただし**ログイン画面の HTML 構造への依存**が残るため、
  実 Keycloak で 1 度通してから自動ケースへ格上げする。実行の場は `integration-stack.yml`（**PR では起動しない**）。
- **T-09（リカバリーコードの 1 回表示）は手動のままである。** 表示は Keycloak のテンプレートが行い、
  realm の宣言では測れない。
