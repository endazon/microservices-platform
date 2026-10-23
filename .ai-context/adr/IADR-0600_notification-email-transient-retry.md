---
title: IADR-0600 通知メールの一過性の送信失敗は deferred へ戻して上限つきで再送し、上限超過だけを failed にする（恒久的な失敗・SMTP 未構成は従来どおり即 failed）
type: impl-adr
status: Accepted
related_ids: [FR-22, SC-10, ADR-0045, ADR-0078, IADR-0215]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md（決定 1-b・2-b・3・8・§結果 フォローアップ 6）
related_specs:
  - ../specs/20261023_2113_email-transient-retry.md
---

# IADR-0600: 通知メールの一過性の送信失敗の上限つき再送（#2113）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2113。親 #2047 の指摘 M-16 のうち「再送」部分）

## 起点・関連

- 関連する計画書 ID: FR-22、SC-10。計画 ADR-0045（決定 1-b・2-b・3・8、§結果 フォローアップ 6「一時的な失敗に対する再送方針」）。
  ADR-0078 は Keycloak の送出経路の改定であり本件の経路（通知サービスの outbox）には及ばない。いずれも変えない。
- 前提とする実装 ADR: [IADR-0215](./IADR-0215_notification-service-and-in-app-delivery.md) 決定 3（outbox・未構成は failed）・決定 4（日次上限は送れた件数で数え、超過は繰り越す）。
- 作業仕様書: [20261023_2113_email-transient-retry](../specs/20261023_2113_email-transient-retry.md)。

## コンテキストと課題

`EmailOutboxDispatcher` は送信の失敗をすべて `Fail`（終端）にしていた。リレーの瞬断・時間切れでも 1 回で `failed` になり、
二度と拾われない。一方、SMTP の実体はまだ無く（IADR-0215 決定 3。利用者裁定 2026-08-15 で見送り）、本番構成でも
`UnconfiguredSmtpEmailTransport` が全件 `smtp-not-configured` を返す。**再送を足すとき、この未構成を一過性と扱うと、
未構成のあいだ全行が毎周期送り直され、監査ログと計器が周期ごとに膨らむ。**

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 失敗はすべて上限つきで再試行する | ✗ 未構成・宛先不正のような恒久的な失敗も上限まで送り直す。未構成のあいだは行ごとに上限回の雑音が出る |
| **B（採用）** | **トランスポートが一過性と判別できた失敗だけを再試行する**。判別できないものは恒久側に倒す | ○ 未構成は 1 行 1 回で終わる。SMTP の実体を入れるときも、判別の責務がトランスポートに閉じる |
| C | 失敗行を別表（DLQ 相当）へ移し、運用者が手で戻す | △ 一過性の瞬断のたびに人手が要る。補助経路にそこまでの運用は過大 |

## 決定

1. **一過性の判別**: `EmailSendResult.Transient`（既定 false）を足し、トランスポートが**一過性と判別できたときだけ**
   `TransientFailure` を返す（接続断・時間切れ・一時的な SMTP 応答＝4xx）。例外の経路では `TimeoutException` /
   `IOException` / `SocketException` / `OperationCanceledException` だけを一過性とする。**それ以外は恒久的な失敗**
   （未構成・宛先未解決・判別できない例外）で、従来どおり即 `failed`。`UnconfiguredSmtpEmailTransport` は恒久的な失敗を返す（変えない）。
2. **差し戻しとバックオフ**: 一過性の失敗は `deferred` に戻し、`NextAttemptAt = now + 基準 × 2^(n-1)`（上限で頭打ち）を置く。
   送出の問い合わせは `NextAttemptAt` が null か `now` 以前の行だけを拾う。`AttemptCount`（送信を試みた回数）を進め、
   `DeferralCount`（日次上限の繰り越し）とは数えを分ける。既定は上限 5 回・基準 5 分・頭打ち 1 時間（最初の失敗から約 75 分で打ち切る）。
   値は `Notification:EmailMaxSendAttempts` / `EmailRetryBaseDelay` / `EmailRetryMaxDelay` で変えられる。
3. **上限超過だけを failed に**: 試行が上限に達した一過性の失敗だけを `failed`（理由 `retry-exhausted:<理由語>`）にし、結末の計器
   `notification.email.total{outcome=failed}` に加えて **`notification.email.retry_exhausted`**（属性 `notification.kind`。既存の
   `notification.email.limit_reached` と同じ命名）で数える。再試行待ちの理由は `transient:<理由語>` とし、日次上限の繰り越し
   （`daily-limit-reached`）と監査ログ上で読み分けられるようにする。
4. **スキーマ**: `EmailOutbox.NextAttemptAt`（timestamptz・null 可）を追加する。**既存行は遡及しない** —— 未送信・繰り越しの行は
   null（＝いつでも拾う）で従来どおり送られ、**既に `failed` の行は `failed` のまま**とする（一過性だったかを後から判別する材料が無い）。

## 理由

- **迷ったら恒久側へ倒す**のは、取り違えたときの害の非対称による。恒久を一過性と誤ると、毎周期の再送が上限まで続く（未構成では全行）。
  一過性を恒久と誤っても、結末は従来どおり `failed` として観測でき、退行は無い。
- 再試行の上限を `AttemptCount` で判定するのは、日次上限による繰り越し（送信を試みていない）を数えに混ぜないためである。
- 上限超過を別の計器にするのは、未構成のあいだ常時出る `failed` の線に、リレー障害の信号を埋もれさせないためである。
- ADR-0045 との整合: 再試行は日次上限を消費しない（IADR-0215 決定 4。上限は送れた件数で数える）ので、決定 1-b（個人アカウント 500 通/日）
  の上限設計を変えない。決定 2-b（接続条件）は SMTP の実体の側で、本件は触れない。決定 8（失敗を隠さない）には、再試行待ちも
  `deferred` として監査ログ・計器に載ることで沿う。

## 結果

- 良い影響: リレーの瞬断で通知メールが失われない。未構成のあいだも雑音は 1 行 1 回に留まる。
- 悪い影響・トレードオフ: 実際のリレー障害では、最初の失敗から最長約 75 分は `failed` に現れない（`deferred` の
  `transient:` 理由として監査ログには出る）。期限つきの通知は再試行待ちのあいだに期限を過ぎれば `dropped` になる。
- 残余リスク: SMTP の実体を入れる実装が 4xx／5xx を正しく判別しないと、本決定の効き目は無い（決定 1 の責務の置き場所）。
- フォローアップ:
  1. SMTP トランスポートの実装と宛先解決の供給元（#2113 の残り。利用者裁定・IADR-0215 決定 3 のまま。計画判断が要る）。
  2. `notification.email.retry_exhausted` の警報規則（SMTP の実体が入り、系列が立つようになってから）。

## 関連

- Supersedes: なし（IADR-0215 決定 3「未設定時は failed」は本決定の下でもそのまま成り立つ）
- Superseded by: なし
