---
title: 作業仕様書 — LLM の逐次応答が途中切断・取り消しで終わっても判明分のトークンと取り消しを計上する（#2116）
type: spec
status: done
related_ids: [FR-10, FR-11, NFR-02, ADR-0010, ADR-0044, ADR-0076, IADR-0601, IADR-0037, IADR-0110, IADR-0212, IADR-0374, IADR-0378, IADR-0466, IADR-0566]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md（決定 3「無音で 0 円として扱わない」・決定 5）
issue: "#2116"
---

# 作業仕様書 — 逐次応答の途中終了・取り消しの計上（#2116）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0601**（新規）に置く。親 #2047（指摘 M-20）。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-10**（利用実績・費用の可視化。issue の起点どおり。コードの既存注記 `FR-10, ADR-0044 決定 1・3 (#443)` と一致）・**FR-11**（送信可否の計器）。
- 計画 ADR: **ADR-0044** 決定 3（単価を解決できなければ警告し、**無音で 0 円として扱わない**）・決定 5（基盤と利用側の費用を合算しない）。**ADR-0076** 決定 4（合成は費用から外す）。いずれも変えない。
  ADR-0044 は逐次応答の途中終了を明示していないが、「費用が静かに過小になる状態を作らない」趣旨は同じであり、本変更はその側へ寄せる（矛盾しない）。
- 実装 ADR: **IADR-0601**（新設）。改める前提 **IADR-0212** 決定 3 の逐次経路への読み（「Done で実数を受け取れたときだけ費用へ積む」）。前提 IADR-0037（逐次）・IADR-0110（計器の値域）・IADR-0374（上流状態の軸）・IADR-0378（合成の除外）・IADR-0466（月次予算アラート）・IADR-0566（無通信の期限）。

## 現状（develop `b45976d8` で再確認）

- `CompletionUseCase.StreamAsync` は上流の失敗を `upstream_error` として数えるが利用実績を積まない。正常経路でも `sawDone` のときだけ `RecordUsage`。
- `catch (Exception ex) when (ex is not OperationCanceledException)` で取り消しは素通りし、`llm.completion.total` にも載らない。呼び出し側が列挙を途中で捨てたとき（gRPC 切断）も同じ。
- `ClaudeProvider.StreamAsync` は `message_start` の入力トークンを最終チャンクにしか載せない（途中で切れると呼び出し側へ届かない）。

## 要件

1. 逐次応答が送信後に途中で終わったとき（上流の切断・呼び出し側の取り消し・最終チャンクの欠落）、判明分のトークンを 1 回だけ計上し、不完全として 1 件数える。
2. 呼び出し側の取り消しを `llm.result=cancelled` として 1 件数える（`upstream_error` と区別する）。取り消しで計上が途切れない。
3. 正常完了は最終チャンクの実数で 1 回だけ計上する（経過報告と二重にしない）。
4. 非ストリーミング経路の挙動は変えない。合成監視は費用へ入れない規則を守る。

## 方式（詳細は IADR-0601）

- プロバイダ: `ClaudeProvider` は `message_start`・`message_delta` で判明した**累計**を本文なしの経過報告チャンク（`Done=false`）で返す。
- 判定器: 経過報告を**置き換えで**保持し、Done が来なければその値で `RecordUsage` を 1 回、`llm.usage.incomplete.total` を 1 件。`settled` 旗で 1 回に限る。
- 取り消し: 外側の `finally` で未決着なら `RecordCompletion(cancelled)` ＋ 判明分。計上は同期の Counter で `ct` を使わない。
- `ct` が立っていない `OperationCanceledException`（HTTP の期限切れ等）は上流の失敗として縮退する。

## 母集合（規則 9・10）

- 走査: `git grep -n "sawDone\|OperationCanceledException\|StreamAsync\|RecordUsage\|ResultUpstreamError"`（`src/platform/backend/Services/LlmGateway`）、`git grep -n "llm.result\|synthetic_excluded\|最終チャンクを受け取れずに"`（`docs/`・`deploy/`・`.ai-context/adr/`）。
- 変更: `CompletionUseCase.cs`・`ClaudeProvider.cs`・`ILlmProvider.cs`（チャンクの意味）・`LlmCompletionMetrics.cs`（`cancelled`）・`LlmUsageMetrics.cs`（不完全の計器）。
- 文書: `docs/observability/llm-usage-and-cost-metrics.md`（「記録しない経路」の逐次の行が本変更で誤りになる＝規則 10）・`docs/observability/llm-completion-metrics.md`・`docs/functional/FR-11_llm-egress-routing.md`（`llm.result` の値域）。
- 除外: `deploy/prometheus/alerts.yml` の `LlmMonthlyBudgetExceeded`（`llm_cost_total` を読むだけで式は不変。判明分が積まれる向きに変わるだけ）・Grafana の `llm-usage` ダッシュボード（パネル追加は 2 経路のパリティを伴うため本 PR の射程外。未決として報告）・確定済みの `.ai-context/specs/`。
- 試験側: 逐次の本文を列挙する既存試験（`ClaudeProviderStreamIdleTimeoutTests`）は経過報告を本文の並びから外して読むよう追随。`ContainSingle(c => c.Done)` 系は不変。
- 利用側（AST）: 逐次（`CompleteStream`）を使っていない（AST の `backend/` を走査し `CompleteStream` の呼び出しは proto 定義のみ）。AST の費用統制は一括応答の `InputTokens/OutputTokens` を自前で計上しており、本変更の影響を受けない。

## 受け入れ基準 → 試験（`StreamUsageOnAbortTests` ほか）

| 基準 | 試験 |
| --- | --- |
| 1 途中切断で判明分を計上・不完全を数える | `上流が途中で切れたとき判明分のトークンを最新の累計で1回計上し不完全として数える`・`Doneが無いまま終わったら…`・送信前の失敗は積まない（過大計上の反対側） |
| 2 取り消しで `cancelled` 1 件・判明分も計上 | `利用者が取り消すとcancelledを1件数え…`・`判明分が無いうちに取り消されたら…`・`呼び出し側が列挙を途中で捨てたときも…`・合成の取り消し |
| 3 正常完了は二重にしない | `正常完了は経過報告を足さずDoneの実数で1回だけ計上する`・`最終イベントの受信直後に列挙を捨てても…` |
| 4 `CancellationTokenSource` を使う | 取り消しの 2 試験 |
| プロバイダの経過報告 | `ClaudeProviderStopReasonTests.StreamAsync_WhenEndTurn_StreamsDeltasAndCarriesStopReason`（入力が本文より前に届く） |

変異: 途中切断の記録を外す→基準 1 が赤／取り消しの記録を外す→基準 2 の 4 件が赤／正常完了で旗を立てない→基準 3 が赤／経過報告を足し合わせる→基準 1 が赤（実測は PR 本文）。
