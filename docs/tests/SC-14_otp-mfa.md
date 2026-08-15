---
title: ワンタイムコード（OTP／多要素認証） テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-14
  - NFR
  - ADR-0026
  - IADR-0197
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md"
related_specs:
  - "../screens/SC-14_otp-mfa.md"
  - "../adr/IADR-0197_realm-rename-and-auth-policy.md"
---

# テスト仕様書: ワンタイムコード（OTP／多要素認証）（SC-14）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-14
- ユースケース（UC）: UC-05
- 受け入れ基準の所在: [`ADR-0026`](../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md) §多要素認証／[`05_screens/01_screens.md` §SC-14](../../planning/projects/microservices-platform/05_screens/01_screens.md)

## テスト対象・範囲

**対象**: `deploy/keycloak/microservices-platform-realm.json` が ADR-0026 の OTP 要件を満たすこと（**静的検査**）。

**対象外（実環境が要る）**: 実際の TOTP 検証・初回セットアップ画面・リカバリーコード表示。
**Keycloak を起動しないと検証できない**（CI は Keycloak を起動しない。実測: `.github/workflows/` に Keycloak を
起動するジョブは無く、`ci.yml:340` の realm 検査は静的検査のみ）。**これらは #438 の射程である。**

## テスト観点

- **realm 設定が確定要件と一致すること**（値の一致。自動）
- **必須アクションの `defaultAction` が真であること** —— `enabled` だけでは未登録者は誘導されない（境界）
- **TOTP の許容ずれが「前後 1 ステップ」であること** —— `otpPolicyLookAheadWindow` は Keycloak では `[-n, +n]` の対称窓である（境界）

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | realm export が存在する | `node scripts/check-realm-constraints.js` | `otpPolicyType = "totp"` | ADR-0026「TOTP による MFA を必須とする」 | 自動 |
| T-02 | 同上 | 同上 | `otpPolicyDigits = 6` | ADR-0026「6桁」 | 自動 |
| T-03 | 同上 | 同上 | `otpPolicyPeriod = 30` かつ `otpPolicyLookAheadWindow = 1` | ADR-0026「時刻ずれは前後1ステップ〔30秒〕まで許容」 | 自動 |
| T-04 | 同上 | 同上 | `requiredActions` に `CONFIGURE_TOTP` があり **`enabled: true` かつ `defaultAction: true`** | ADR-0026「未登録者はログイン時に初回セットアップへ誘導する」 | 自動 |
| T-05 | 同上 | 同上 | `otpPolicyCodeReusable = false` | 同一コードの再利用を許さない（TOTP の前提） | 自動 |
| T-06 | Keycloak 稼働・TOTP 未登録の利用者 | SC-13 でログインする | `CONFIGURE_TOTP` の初回セットアップへ誘導される | ADR-0026「未登録者は…誘導する」 | **手動（実環境・#438）** |
| T-07 | Keycloak 稼働・TOTP 登録済 | 1 ステップ前／後のコードを入力する | いずれも受理される | ADR-0026「前後1ステップまで許容」 | **手動（実環境・#438）** |
| T-08 | 同上 | 2 ステップ前のコードを入力する | 拒否される | 同上（**境界の外**） | **手動（実環境・#438）** |
| T-09 | 初回登録を完了する | 登録完了画面を確認する | リカバリーコードが **1 回のみ**表示される | `01_screens.md` §SC-14 | **手動（実環境・#438）** |

## テストデータ

`deploy/keycloak/microservices-platform-realm.json`（リポジトリ内。追加のテストデータは要らない）。

## 関連仕様

- 画面仕様書: [SC-14](../screens/SC-14_otp-mfa.md)
- 実装 ADR: [IADR-0197](../adr/IADR-0197_realm-rename-and-auth-policy.md)

## 未決事項

- T-06〜T-09 は実環境が要る。**#438 が Keycloak を立てた時点で実施する。**
