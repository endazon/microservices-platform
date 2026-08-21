---
title: ワンタイムコード（OTP／多要素認証） テスト仕様書
type: test-spec
status: draft
created: 2026-08-15
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [SC-13, SC-14, UC-05]
adrs: [ADR-0026]
iadrs: [IADR-0197]
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
| T-06 | Keycloak 稼働・TOTP 未登録の利用者 | ログイン画面からログインする | `CONFIGURE_TOTP` の初回セットアップへ誘導される | 計画 ADR「未登録者は…誘導する」 | **手動（実環境・#438）** |
| T-07 | Keycloak 稼働・TOTP 登録済 | 1 ステップ前／後のコードを入力する | いずれも受理される | 計画 ADR「前後1ステップまで許容」 | **手動（実環境・#438）** |
| T-08 | 同上 | 2 ステップ前のコードを入力する | 拒否される | 同上（**境界の外**） | **手動（実環境・#438）** |
| T-09 | 初回登録を完了する | 登録完了画面を確認する | リカバリーコードが **1 回のみ**表示される | 計画側の画面設計 §ワンタイムコード（OTP） | **手動（実環境・#438）** |

## テストデータ

`deploy/keycloak/microservices-platform-realm.json`（リポジトリ内。追加のテストデータは要らない）。

## 関連仕様

- 画面仕様書: [ワンタイムコード（OTP）](../screens/SC-14_otp-mfa.md)
- 実装 ADR: レルムを `platform` へ改名し、計画 ADR の認証ポリシーを realm へ投入する

## 未決事項

- T-06〜T-09 は実環境が要る。**#438 が Keycloak を立てた時点で実施する。**
