---
title: 通知のメール補助経路 —— 一過性の送信失敗を上限つきで再送し、上限超過だけを failed にする（#2113）
type: spec
status: done
related_ids: [FR-22, SC-10, ADR-0045, ADR-0078, IADR-0215, IADR-0600]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-22）
  - planning:projects/microservices-platform/07_adr/ADR-0045_mail-delivery-smtp-relay.md（決定 1-b・2-b・3・8・§結果 フォローアップ 6）
issue: "#2113"
---

# 仕様書: 通知メールの一過性の送信失敗の再送（#2113。親 #2047 の指摘 M-16 の「再送」部分）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-22（利用者本人への通知。受け入れ基準「メールが送れなくてもアプリ内通知は届く」「送信上限を超える通知が静かに落ちない」）。
- 画面: SC-10（運用ダッシュボードで送信失敗を観測する面）。
- 計画 ADR: ADR-0045（決定 1-b・2-b・3・8、§結果 フォローアップ 6「一時的な失敗に対する再送方針を運用設計で定める」）、ADR-0078（決定 2-b・8 の部分改定。本件は Keycloak の経路ではなく通知サービスの経路であり、触れない）。
- 実装 ADR: [IADR-0215](../adr/IADR-0215_notification-service-and-in-app-delivery.md) 決定 3・4（outbox・未構成は failed）を前提とし、本件の判断は [IADR-0600](../adr/IADR-0600_notification-email-transient-retry.md) に記録する。

## 目的・背景

`EmailOutboxDispatcher` は `pending` / `deferred` だけを拾い、送信の失敗はすべて `Fail`（終端）にしていた。
リレーの瞬断・時間切れでも 1 回で `failed` になり、二度と送られない。ADR-0045 §結果 フォローアップ 6 が
残した「一時的な失敗に対する再送方針」が実装側に無い状態である。

## 対象範囲

- 対象: 一過性の失敗の判別（トランスポートの結果の `Transient`・例外の型）、`deferred` への差し戻しと
  次回時刻（バックオフ）、試行の上限、上限超過の計器、列の追加（マイグレーション）、試験、文書（FR-22 の機能・データ・
  試験仕様書、運用仕様書）。
- **対象外（触らない）**: SMTP トランスポートの実体と宛先解決の供給元。利用者裁定（2026-08-15、IADR-0215 決定 3）で
  見送られており、計画判断が要る（issue 本文「計画との関係」）。未構成用トランスポートの挙動は**恒久的な失敗**として据え置く。

## 母集合（規則 9: 誤りの側の文字列で走査）

`git grep -n -E "EmailOutboxStatus\.Failed|outbox.*failed|failed.*outbox|notification\.email\.(total|failed)|smtp-not-configured|AttemptCount" -- docs src deploy`（.cs 以外）と、
FR-22 系の 3 文書・観測仕様・deploy での `failed|再送|再試行` の走査の結果:

| 箇所 | 扱い |
| --- | --- |
| `docs/functional/FR-22_user-notifications.md` の流れ図（`S -- 失敗 --> F`）と例外表「メール送信が失敗した → failed」 | **直す**（一過性／恒久の 2 行に分ける） |
| `docs/data/notification.md` の EmailOutbox 表 | **直す**（`NextAttemptAt` を足し、`AttemptCount` の意味を補う） |
| `docs/tests/FR-22_user-notifications.md` T-10 | **据え置き**（T-10 の 4 面は恒久的な失敗であり、本件後も `failed` のまま。試験も無変更で緑）。T-40 を足す |
| `docs/operations/operations.md` | **足す**（未構成のあいだ `notification.email.failed` が出続けることは**どこにも書かれていなかった**） |
| `deploy/`（Grafana・Prometheus の警報） | 通知メールの計器を参照する規則は無い。**変更なし** |
| `IADR-0215`（決定 3「未設定時は failed」） | **凍結記録のため書き換えない**。本件後も「未構成は failed」は成り立つ |

規則 10（この変更で新たに誤りになる自分の記述）: 計器の説明「4 つの結末」は結末の値域としては変わらない（再試行待ちは `deferred`）。
`DispatchSummary` は `RetryScheduled`／`RetryExhausted` を足したが、既存の 4 値の意味は変わらない（前者は Deferred の、後者は Failed の内訳）。

## 設計

- `EmailSendResult` に `Transient`（既定 false）と `TransientFailure(reason)` を足す。**判別はトランスポートの責務**で、
  判別できないものは恒久側（false）に倒す。
- 例外の経路: `TimeoutException` / `IOException` / `SocketException` / `OperationCanceledException` だけを一過性とする。
  それ以外（例: `InvalidOperationException`）は従来どおり恒久的な失敗。
- `EmailOutboxEntry` に `NextAttemptAt`（timestamptz・null 可）と `ScheduleRetry(at, next, reason)` を足す。
  `ScheduleRetry` は `deferred` にし `AttemptCount` を進める（`DeferralCount` は上限の繰り越し専用のまま）。
  `MarkSent` / `Fail` / `Defer` は `NextAttemptAt` を消す。
- 送出の問い合わせに `NextAttemptAt == null || NextAttemptAt <= now` を足す。
- 一過性の失敗で `AttemptCount + 1 < 上限` なら `ScheduleRetry(now, now + 待ち)`、上限に達したら `Fail`（理由 `retry-exhausted:<理由語>`）
  ＋ 計器 `notification.email.retry_exhausted`（属性 `notification.kind`）。待ちは `基準 × 2^(n-1)`、上限で頭打ち。
- 設定: `Notification:EmailMaxSendAttempts`（既定 5）・`EmailRetryBaseDelay`（既定 5 分）・`EmailRetryMaxDelay`（既定 1 時間）。
- マイグレーション `AddEmailOutboxNextAttemptAt`: 列の追加のみ（null 可）。**既存行は遡及しない** —— pending／deferred は null
  ＝従来どおり拾われ、**既に failed の行は failed のまま**（一過性だったかを後から判別する材料が無い）。

## 受け入れ基準

- [x] 1. 一過性の失敗が `deferred` に戻り、次の周期で再送される（`EmailRetryTests`。結果で返す形と 4 種の例外の形）。
- [x] 2. 上限を超えたものだけが `failed` になり、計器 `notification.email.retry_exhausted` が 1 増える（上限 3 で 1・2 回目は増えない）。
- [x] 3. 恒久的な失敗・未構成は即 `failed` で、再試行されない（未構成・恒久の結果・判別できない例外・宛先未解決。以後 3 周期で送信 0 回）。
- [x] 4. バックオフで次回時刻より前には拾わない（1 秒前は送らず、ちょうどで送る。待ちは倍々で上限頭打ち）。
- [x] 変異: 再試行の分岐を外すと 1 を含む 8 件が赤／問い合わせの次回時刻の条件を外すと 4 が赤／未構成を一過性に変えると 3 が赤／
  計器の加算を外すと 2 が赤／例外の判別を「すべて一過性」にすると既存の T-10（例外）と 3 が赤。

## テスト方針

`Tests/Features/Notifications/DispatchEmails/EmailRetryTests.cs`（単体・InMemory）。計器は `MeterListener` で、
試験ごとに一意な `kind` に絞って数える（並行する試験の計器と混ざらない）。既存の `EmailIndependenceTests`（T-10）・
`EmailSendRateTests`（T-11）は無変更で緑であること。

## 計画書との差異

- 差異: なし。ADR-0045 決定 1-b（個人アカウント・500 通/日）とは、再試行が日次上限を消費しない（上限は送れた件数で数える）ので
  衝突しない。決定 2-b（`smtp.gmail.com:587`・STARTTLS）は SMTP の実体の側であり、本件は触れない。決定 8（失敗を隠さない）には、
  再試行待ちも `deferred` として監査ログ・計器に載り、上限超過は別の計器でも数えることで沿う。

## 未決事項

- SMTP トランスポートの実装と宛先解決の供給元（利用者裁定・IADR-0215 決定 3 のまま。#2113 の残り）。実装する際は、SMTP の 4xx 応答・接続断を
  `EmailSendResult.TransientFailure` で返すこと（5xx は恒久）。
- 上限超過の警報（`notification.email.retry_exhausted` の増加）は置いていない。SMTP の実体が無いあいだは系列が立たず、規則を置いても検証できないため。
