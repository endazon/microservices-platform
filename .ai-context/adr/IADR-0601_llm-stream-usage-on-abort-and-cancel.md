---
title: IADR-0601 LLM の逐次応答が Done に届かずに終わっても判明分のトークンを 1 回計上し、不完全として数える。呼び出し側の取り消しは llm.result=cancelled として数える
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, NFR-02, ADR-0010, ADR-0044, ADR-0076, IADR-0037, IADR-0110, IADR-0212, IADR-0374, IADR-0378, IADR-0466, IADR-0566]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md（決定 3・5）
related_specs:
  - ../specs/20261023_2116_llm-stream-usage-on-abort.md
---

# IADR-0601: 逐次応答の途中終了・取り消しの計上（#2116）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2116。親 #2047 の指摘 M-20）

## 起点・関連

- 起点 issue: #2116（親 #2047）。
- 関連する計画書 ID: FR-10（利用実績）・FR-11（送信可否の計器）・NFR-02。計画 ADR: ADR-0044 決定 3（**無音で 0 円として扱わない**）・決定 5・ADR-0076 決定 4。いずれも変えない。
- 改める実装 ADR: [IADR-0212](./IADR-0212_llm-output-token-histogram.md) 決定 3 の逐次経路への読み（`CompletionUseCase` の「Done で実数を受け取れたときだけ費用へ積む」）。**出力トークンの分布（Histogram）に 0 を埋めない規則は変えない。**
- 前提: [IADR-0037](./IADR-0037_llm-sse-streaming.md)（逐次）・[IADR-0110](./IADR-0110_llm-completion-stop-reason-metrics.md)（値域を閉じる）・[IADR-0374](./IADR-0374_llm-upstream-status-metric-axis.md)・[IADR-0378](./IADR-0378_synthetic-traffic-marker-and-exclusion.md)（合成の除外）・[IADR-0466](./IADR-0466_llm-monthly-budget-alert-configurable-limit.md)（月次予算アラート）・[IADR-0566](./IADR-0566_stream-idle-timeout.md)（無通信の期限は TimeoutException）。
- 作業仕様書: [20261023_2116_llm-stream-usage-on-abort](../specs/20261023_2116_llm-stream-usage-on-abort.md)。

## コンテキストと課題

- 逐次応答（gRPC `CompleteStream`）は、上流が途中で切れると `upstream_error` を数えるだけで利用実績を積まず、正常経路でも Done を受け取れたときだけ積んでいた。
  Claude は `message_start` の時点で入力トークンを返しており、課金も始まっている。それでも費用は 0 のまま、**過小であることを示す系列も無かった**（ADR-0044 決定 3 の趣旨に反する）。
- 取り消し（`OperationCanceledException`）は catch を素通りし、`llm.completion.total` にも載らなかった。呼び出し側が列挙を途中で捨てた（gRPC の切断）ときも同じ。

## 検討した選択肢

1. **判明分だけを計上する** —— 入力トークンは計上できるが、出力は通常 `message_delta`（末尾）でしか届かないので、途中で切れた送信の出力分は欠ける。**下限であることが金額から読めない。** 単独では採らない。
2. **不完全の件数だけを数える** —— 過小であることは読めるが、判明している入力分まで 0 円のままで、月次予算の判定に一切入らない。単独では採らない。
3. **判明分を計上し、あわせて不完全を数える（採用）** —— 金額は下限として予算判定に入り、下限であることは別の系列で読める。単価を解決できなかったときの扱い（金額は積まず、件数で知らせる）と同じ「金額の確からしさを件数で示す」形。
4. 出力トークンを本文の文字数から推定する —— 推定値が実数と同じ計器へ混ざり、どこまでが推定か区別できない。採らない。

## 決定

1. **プロバイダは判明したトークンの累計を経過報告として返す。** `CompletionChunk` の `Done=false` でトークン数を持つチャンクは「その時点の累計」（差分ではない）。`ClaudeProvider.StreamAsync` は `message_start`（入力）と `message_delta`（出力）を受けた時点で本文なしの経過報告を返す。
   判定器は**置き換えで**保持し（足さない）、利用者へは流さない。既定実装（Done 1 チャンク）のプロバイダは経過報告を持たない。
2. **Done に届かずに終わった送信は、判明分で `RecordUsage` を 1 回呼び、`llm.usage.incomplete.total` を 1 件数える。**
   - 属性: `RecordUsage` と同じ 4 軸（用途・モデル・プロバイダ・機密区分）＋ `llm.incomplete_reason`（`upstream_error` / `cancelled` / `no_final_chunk`）＋ `llm.usage_coverage`（`partial`＝判明分を積んだ / `none`）。値域は閉じる。
   - 対象: 上流の途中切断（**1 チャンクでも受け取った後**の失敗）・呼び出し側の取り消し・例外なく終わったが Done が無い。**送信が成立する前の上流失敗**（最初の `MoveNextAsync` での 429 等）は一括経路の上流失敗と同じく何も積まない（課金されていない呼び出しで不完全を膨らませない）。**取り消しは受け取りの有無に関わらず数える**（要求が上流へ届いていれば課金は始まり得る）。
   - 合成監視は費用へ入れず（IADR-0378）、`llm.usage.synthetic_excluded.total` を 1 件積む。不完全にも数えない（費用の確からしさの系列なので）。
3. **呼び出し側の取り消しは `llm.completion.total{llm.result="cancelled"}` を 1 件数える。** 送信は成立したが応答を受け取り切っていないので `sent` に入れず、上流の失敗ではないので `upstream_error` に入れない。応答の `FailureKind` ではない（返す相手がいない）。
   - 判定: `ct` が立った `OperationCanceledException` はそのまま伝播させ、外側の `finally` が「未決着」を取り消しとして数える。列挙を途中で捨てられた（`DisposeAsync` だけが来た）ときも同じ `finally` を通る。
   - **`ct` が立っていない `OperationCanceledException`**（HTTP の期限切れ等）は上流の失敗として `upstream_error` へ縮退する（従来は例外のまま伝播していた。「縮退は例外にしない」IADR-0400 決定 5 に揃える）。
   - **記録は同期の計器（`Counter.Add`）で、取り消された `ct` を使わない。** 記録のための別の期限は要らない（I/O を伴わないので、取り消しで記録が途切れる経路が無い）。
4. **計上は 1 ストリームにつき 1 回。** 計器と利用実績を書き終えたら `settled` を立て、最終イベントの `yield` の**前に**立てる。呼び出し側が最終イベントの受信中に列挙を捨てても、`finally` は 2 回目を書かない。
   正常完了は Done の実数で 1 回だけ積み、経過報告の値は使わない。

## 月次費用の上限判定（ADR-0044・IADR-0466）への算入

- 判明分は `llm.cost.total` に積まれるので、上限アラート `LlmMonthlyBudgetExceeded`（直近 30 日の `llm_cost_total` 対 `llm_budget_monthly_limit`）と月次の手動確認にそのまま入る。**式は変えない。**
- 欠けるのは途中で切れた送信の出力分（と、入力が判明する前に取り消された送信の全部）である。**`llm_usage_incomplete_total` が 0 でない間、判定は過小側**であり、これを読む場所は利用実績の仕様書の「監視観点」に置いた。
- 計上の向きは**増える側だけ**である（従来 0 だった送信に判明分が足される。従来積んでいた正常完了の値は変わらない）。

## 理由

- 判明分は事実として課金されている量の下限であり、0 より実態に近い。下限であることを別の系列で示せば「静かに過小」にはならない。
- 取り消しを専用の値で数えれば、利用者の停止が上流障害の率に混ざらず、`sent` の分母（拒否率・出力分布）も汚れない。

## 結果

- 逐次応答の途中終了・取り消しが費用と件数の両方に現れる。Grafana の `llm-usage` ダッシュボードへ不完全の系列を足すのは後続（2 経路のパリティを伴う）。
- 利用側（AST）は逐次を使っておらず、費用統制は一括応答のトークン数を自前で計上しているため影響を受けない。基盤の `llm_cost_total` は判明分だけ増える向きに変わる。

## 残余リスク

- (a) 出力トークンは `message_delta` でしか届かないので、途中切断・取り消しの出力分は依然として欠ける（`llm.usage_coverage=partial` でも下限）。
- (b) Claude 以外のプロバイダ（既定実装）は経過報告を持たないので、途中で終わると `coverage=none` で件数だけが残る。
- (c) 取り消しの判定は「判定器が未決着のまま列挙を終えた」で行う。上流の失敗はすべて catch が拾うので、`finally` へ未決着で来る経路は取り消し（`ct`）と呼び出し側の破棄だけである。新しい終了経路を足すときは `settled` を立てること。
