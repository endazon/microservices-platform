---
title: パスワードリセット テスト仕様書
type: test-spec
status: draft
created: 2026-08-15
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [SC-10, SC-15, UC-05]
adrs: [ADR-0026, ADR-0045]
iadrs: [IADR-0197]
specs: [SC-15_password-reset]
issues: [#438]
-->

# テスト仕様書: パスワードリセット

## 起点となる計画書（トレーサビリティ）

- 画面: パスワードリセット
- ユースケース: ABAC 権限を管理する
- 受け入れ基準の所在: 認証 UX とアカウント管理の計画 ADR §パスワード・ロックアウト／メール配信の計画 ADR／計画側の画面設計 §パスワードリセット

## テスト対象・範囲

**対象**: `deploy/keycloak/microservices-platform-realm.json` が認証 UX の計画 ADR のパスワード・ロックアウト・有効期限の確定要件を満たすこと（**静的検査**）と、
**パスワードポリシーの「3 種以上」判定が正しいこと**（正規表現の**単体試験**）。

**対象外（実環境が要る）**: メール送出・存在秘匿の実挙動・全セッション失効・リンク有効期限の実測。
**`smtpServer` が未設定であるため、メールを伴う経路は原理的に検証できない**（[画面仕様書 §メール送出は成立していない](../screens/SC-15_password-reset.md)）。**#438 の射程である。**

## テスト観点

- **realm 設定が確定要件と一致すること**（値の一致。自動）
- **「4 種のうち 3 種以上」という選言が正しく判定されること**（**2 種は拒否・3 種は受理**の境界。自動）
- **`resetPasswordAllowed = true` を「実装済み」と数えないこと** —— これは Keycloak の既定値である（回帰防止）

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | realm export が存在する | `node scripts/check-realm-constraints.js` | `passwordPolicy` に `length(12)` を含む | 計画 ADR「12 文字以上」 | 自動 |
| T-02 | 同上 | 同上 | `passwordPolicy` に `passwordHistory(5)` を含む | 計画 ADR「直近 5 世代と不一致」 | 自動 |
| T-03 | 同上 | 同上 | `passwordPolicy` に `regexPattern(...)` を含む | 計画 ADR「3 種以上」（組み込みポリシーでは表せないため正規表現） | 自動 |
| T-04 | 同上 | `node scripts/check-realm-constraints.js --self-test` | **小+数の 2 種は拒否**・**小+大+数の 3 種は受理**・**4 種は受理**・**1 種は拒否** | 計画 ADR「3 種以上」の**境界** | 自動 |
| T-05 | 同上 | `node scripts/check-realm-constraints.js` | `actionTokenGeneratedByUserLifespan = 1800` | 計画 ADR「リセットリンクの有効期限は 30 分」 | 自動 |
| T-06 | 同上 | 同上 | `bruteForceProtected = true` かつ `failureFactor = 5` | 計画 ADR「5 回連続失敗」 | 自動 |
| T-07 | 同上 | 同上 | `waitIncrementSeconds = 900` かつ `maxFailureWaitSeconds = 900` かつ `permanentLockout = false` | 計画 ADR「15 分の一時ロック」（**永久ロックではない**） | 自動 |
| T-08 | 同上 | 同上 | `requiredActions` に `UPDATE_PASSWORD` があり `enabled: true` | メール配信の計画 ADR 決定 9-b の代替手順が realm 設定だけで成立すること | 自動 |
| T-09 | 同上 | 同上 | `rememberMe = true` かつ `ssoSessionIdleTimeoutRememberMe = 2592000` | 計画 ADR「このデバイスを記憶は 30 日」 | 自動 |
| T-10 | Keycloak 稼働・`smtpServer` 設定済 | 未登録アドレスで申請する | 「メールを送信しました」と表示される（**存在秘匿**） | 計画側の画面設計 §パスワードリセット | **手動（実環境・#438）** |
| T-11 | 同上 | 登録済アドレスで申請しリンクを 30 分経過後に開く | 期限切れとして拒否される | 計画 ADR「有効期限 30 分」 | **手動（実環境・#438）** |
| T-12 | 同上 | リセットを完了する | **当該利用者の全セッションが即時失効する** | 計画 ADR §セッション | **手動（実環境・#438）** |
| T-13 | 同上 | 送信を失敗させる | 利用者向け文言は変わらず、**監査ログへ記録**され運用ダッシュボードで観測できる | メール配信の計画 ADR 決定 8 | **手動（実環境・#438）** |
| T-15 | Keycloak 稼働・テーマ実装済 | 新パスワード設定画面を開く | ポリシーが**「英大文字・小文字・数字・記号のうち3種以上」と列挙**して表示される | `01_screens.md` §モック間相違の確定 ⑤（**本項のみ wireframe を正とする**） | **手動（実環境・#438）** |
| T-16 | Keycloak 稼働・`smtpServer` 設定済 | リセットメールを受信する | 本文が**リンクと有効期限のみ**である（余分な情報を含まない） | メール配信の計画 ADR 決定 7 | **手動（実環境・#438）** |
| T-14 | Keycloak 稼働・SMTP 停止 | 管理者が一時パスワードを発行し `UPDATE_PASSWORD` を付与する | 利用者は初回ログイン時にパスワード変更を強制される | メール配信の計画 ADR 決定 9-b | **手動（実環境・#438）** |

## テストデータ

`deploy/keycloak/microservices-platform-realm.json`。パスワードポリシーの境界試験は `--self-test` に埋め込む
（**実際に投入している正規表現そのものを試験対象にする** —— 別の正規表現を書き写すと、投入値が変わったときに試験が追随しない）。

## 関連仕様

- 画面仕様書: [パスワードリセット](../screens/SC-15_password-reset.md)
- 実装 ADR: レルムを `platform` へ改名し、認証 UX の計画 ADR のポリシーを realm へ投入する

## 未決事項

- T-10〜T-14 は実環境が要る。**`smtpServer` の供給（3 点）が前提**であり、#438 の射程である。
