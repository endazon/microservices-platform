---
title: パスワードリセット 画面仕様書
type: screen-spec
status: draft
related_ids:
  - SC-15
  - SC-13
  - SC-10
  - NFR
  - ADR-0026
  - ADR-0045
  - IADR-0197
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md"
related_specs:
  - "../adr/IADR-0197_realm-rename-and-auth-policy.md"
  - "../tests/SC-15_password-reset.md"
  - "./SC-14_otp-mfa.md"
---

# 画面仕様書: パスワードリセット（SC-15）

> **本仕様書は realm 設定の側だけが実装済みである。** 画面（Keycloak テーマ）とメール送出は未実装であり、
> **担当は #438**（計画 決定 30）。本書は #578 が引き受けた下位タスク＝「realm 設定と画面仕様書の作成」の成果物である。

## ★ 前提の訂正 —— `resetPasswordAllowed = true` は「実装済み」ではない

`realm.json` の `resetPasswordAllowed` は**改名前から `true` であった**が、**これは Keycloak の既定値**であって
SC-15 の実装ではない。**この 1 つの真を見て「リセットは実装済み」と読むと、パスワードポリシーも
リンク有効期限もセッション失効も存在秘匿も無い状態を「済み」と数えることになる**（#578 が指摘した型）。

**改名前の実測（2026-08-15）**: `passwordPolicy` / `otpPolicyType` ほか OTP 系 / `requiredActions` /
`bruteForceProtected` / `failureFactor` / `actionTokenGeneratedByUserLifespan` / `smtpServer` / `rememberMe` の
**8 項目すべてが未設定**。`resetPasswordAllowed = true` だけが真であった。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-15**（パスワードリセット）。戻り先は SC-13（ログイン）、送信失敗の観測は [SC-10](./SC-10_operations-dashboard.md)（運用ダッシュボード）
- 関連ユースケース（UC）: UC-05
- 関連機能要求（FR）: 非機能要件「セキュリティ: 認証・認可」
- 計画書リンク: [`05_screens/01_screens.md` §SC-15](../../planning/projects/microservices-platform/05_screens/01_screens.md)／[`ADR-0026`](../../planning/projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md)／[`ADR-0045`](../../planning/projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md)

## 画面概要・目的

メール経由の自己パスワードリセット（Keycloak の `reset-credentials` フロー）。

- 共通シェル: **適用外**（Keycloak テーマ）
- 配信ホスト: 認証基盤ホスト `auth.example.co.jp`

## レイアウト / 主要素

| 要素 | 説明 |
| --- | --- |
| メールアドレス入力 | 申請フォーム |
| 送信完了表示 | **アドレスの登録有無によらず常に「メールを送信しました」**（存在秘匿） |
| 新パスワード設定 | メール内リンクから遷移。**ポリシーを画面に表示する** |

## 表示・入力項目

| 項目 | 種別 | 必須 | 初期値 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- | --- |
| メールアドレス | メール | 必須 | 空 | メール形式 | 送信後も存在有無を返さない |
| 新パスワード | パスワード | 必須 | 空 | 12 文字以上・英大/小/数字/記号のうち **3 種以上**・**直近 5 世代と不一致** | ポリシーを画面に表示する |
| 新パスワード（確認） | パスワード | 必須 | 空 | 新パスワードと一致 | — |

## バリデーション

| 項目 | 条件 | エラーメッセージ |
| --- | --- | --- |
| メールアドレス | **存在秘匿。登録有無によらず同一の完了文言** | 「メールを送信しました」で固定 |
| リセットリンク | **有効期限 30 分** | 期限切れは再申請を促す |
| 新パスワード | 12 文字以上・3 種以上・直近 5 世代と不一致 | ポリシー違反の内容を表示 |

**realm 側の実装値**（`deploy/keycloak/microservices-platform-realm.json`。[IADR-0197](../adr/IADR-0197_realm-rename-and-auth-policy.md)）:

| キー | 値 | 対応する ADR-0026 の確定要件 |
| --- | --- | --- |
| `passwordPolicy` | `length(12) and passwordHistory(5) and regexPattern(...)` | 12 文字以上・直近 5 世代・**3 種以上** |
| `actionTokenGeneratedByUserLifespan` | `1800` | **リセットリンクの有効期限 30 分** |
| `bruteForceProtected` / `failureFactor` | `true` / `5` | **5 回連続失敗** |
| `waitIncrementSeconds` / `maxFailureWaitSeconds` | `900` / `900` | **15 分の一時ロック** |
| `permanentLockout` | `false` | 永久ロックにしない（15 分で解除される） |
| `requiredActions[UPDATE_PASSWORD]` | `enabled: true` | **ADR-0045 決定 9-b の代替手順**（管理者が一時パスワード発行時に付与する） |
| `resetPasswordAllowed` | `true` | **既定値。SC-15 の実装ではない**（上記「前提の訂正」） |

> **「3 種以上」は Keycloak の組み込みポリシーでは表せない。** `upperCase(n)` / `lowerCase(n)` / `digits(n)` /
> `specialChars(n)` はいずれも **AND** であり、「4 種のうち 3 種」という選言を表現できない。
> **`regexPattern` の先読みで 4 通りの組み合わせを選言として書いている**（[IADR-0197](../adr/IADR-0197_realm-rename-and-auth-policy.md) 決定 3）。

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| 申請（メールアドレス送信） | リセットメールを送信。**登録有無によらず同一表示** | 送信完了表示 |
| メール内リンク | 新パスワード設定フォーム（有効期限 30 分） | 新パスワード設定 |
| 設定完了 | **既存の全セッションを即時失効させる** | SC-13 |

## 画面遷移

```mermaid
flowchart LR
  SC13[SC-13 ログイン] -->|パスワードを忘れた| REQ[SC-15 申請]
  REQ --> DONE[送信完了表示<br/>存在秘匿・常に同一文言]
  DONE -.->|メール内リンク・30 分| SET[新パスワード設定]
  SET -->|完了・全セッション失効| SC13
  REQ -.->|送信失敗| AUDIT[監査ログ]
  AUDIT --> SC10[SC-10 運用ダッシュボード<br/>死活と失敗率]
```

## 権限・表示条件

未認証で到達できる（ログインできない利用者が使う画面であるため）。

## ルート

| 用途 | ルート |
| --- | --- |
| 申請 | `auth.example.co.jp` の `/realms/platform/login-actions/reset-credentials` |
| メールリンクからの新パスワード設定 | `auth.example.co.jp` の `/realms/platform/login-actions/action-token?...` |

## ★ メール送出は成立していない —— 足りないもの

**`smtpServer` は投入していない。** 実環境の接続値が要るためである（利用者裁定 2026-08-15「実環境が要るものは触らない」）。
**ADR-0045 決定 1（組織が管理するメールテナント＝ go-live では Google Workspace への SMTP リレー。第三者の配信 SaaS は用いない）**を満たすために足りないものは次の 3 つである。

| # | 不足 | 供給元 |
| --- | --- | --- |
| 1 | SMTP ホスト / ポート（`smtp.gmail.com` / 587 想定）と STARTTLS 設定 | 実環境 |
| 2 | 送信元アドレスと**アプリパスワード相当の認証情報** | 組織のメールテナント。**平文コミット禁止**のため Secret 経由 |
| 3 | 送信元の表示名・返信先 | 運用判断 |

**この 3 つが揃うまで SC-15 のメール送出は成立しない。**

### 代替（メール基盤が止まったとき）は realm 設定だけで成立する

ADR-0045 決定 9-b の**管理者による本人確認済みリセット**（申請〔本人〕→ 上長が本人性を保証 → 管理者が実行）は、
Keycloak 管理コンソールでの一時パスワード発行と `UPDATE_PASSWORD` 必須アクションで成立し、**SMTP も外部サービスも要さない**。
**本作業で `UPDATE_PASSWORD` を有効な必須アクションとして投入済み**である。一時パスワードは口頭（対面・電話）で伝え、
申請者・承認者・実行者を監査ログへ残す。

## 計画（モックアップ・画面設計）との対応

> **判定基準は [SC-14](./SC-14_otp-mfa.md) の同節と共通である。**

| 計画側の要素 | 実装 | 満たしていない条件 / 理由 | 計画側の該当箇所 |
| --- | --- | --- | --- |
| 12 文字以上・3 種以上・直近 5 世代と不一致（**強制**） | **する** | `passwordPolicy` に投入済み（`regexPattern` の選言で 3 種以上を表現） | `01_screens.md` §SC-15 |
| **新パスワード設定画面のポリシー表示**（主要素「ポリシー表示付き」） | **しない** | **テーマ未実装**（#438）。計画は文言まで規範化している —— **§モック間相違の確定 ⑤ は本項のみ wireframe を正とし、文字種を「英大文字・小文字・数字・記号のうち3種以上」と列挙せよ**と定める。**強制（realm）と表示（画面）は別物であり、前者が済んでも後者は残る** | `01_screens.md` §モック間相違の確定 ⑤ ／ §SC-15 |
| リセットリンクの有効期限 30 分 | **する** | `actionTokenGeneratedByUserLifespan = 1800` | 同上 |
| 5 回失敗で 15 分ロック | **する** | `bruteForceProtected` / `failureFactor` / `waitIncrementSeconds` | `ADR-0026` §パスワード・ロックアウト |
| メール経由の自己リセット | **しない** | **`smtpServer` 未設定**（実環境の値が要る。上記 3 点） | `01_screens.md` §SC-15 |
| 存在秘匿（常に「メールを送信しました」） | **一部する** | **Keycloak の `reset-credentials` フローは既定で存在秘匿である**が、**文言のブランド適用はテーマ未実装**（#438） | 同上 |
| 完了時に全セッションを失効 | **しない** | Keycloak の標準挙動に依存する部分と作り込みの境界が未確定。**#438 で実環境確認が要る** | 同上 |
| 送信失敗を監査ログへ記録し SC-10 で観測 | **しない** | 送信経路そのものが未設定のため成立しない。ADR-0045 決定 8 | `01_screens.md` §SC-15 |
| メール本文はリンクと有効期限のみ | **しない** | 同上（テンプレートはテーマの一部）。ADR-0045 決定 7 | 同上 |
| 管理者による本人確認済みリセット（代替） | **一部する** | **`UPDATE_PASSWORD` は投入済み**。運用手順書（`docs/operations/`）への記載は #438 で行う | `01_screens.md` §SC-15 |

## 関連仕様

- 実装 ADR: [IADR-0197](../adr/IADR-0197_realm-rename-and-auth-policy.md)
- テスト仕様書: [SC-15](../tests/SC-15_password-reset.md)
- 画面仕様書: [SC-14 ワンタイムコード](./SC-14_otp-mfa.md)

## 未決事項

- **`smtpServer` の投入時期**。実環境の値が供給されてから。#438 の射程。
- **全セッション失効の実現方式**（Keycloak の標準挙動で足りるか、作り込みが要るか）。実環境での確認が要る。
