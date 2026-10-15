---
title: 計測用の前倒しの配線・周期継続・停止要求を試験で固定し、RerankCompletionTransportTests の実時間の競争を除く（#1890）
type: spec
status: done
related_ids: [NFR-16, FR-03, FR-11, FR-19, FR-22, ADR-0117, ADR-0127, IADR-0530, IADR-0431, IADR-0498]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs: []
issue: "#1890"
---

# 仕様書: 計測用の前倒しの試験の補強と、再順位付けの輸送の試験の不安定さの是正（#1890）

## 起点

- #1890（#1889 の独立監査。マージ済み 751780b8 時点で変異 M4・M5・M6 が既存の試験で生き残った）。
- 対象の実装: #1887 / #1889 の計測専用の前倒し（`PrivateNotes:Maintenance:InitialRunDelaySeconds`。IADR-0530 決定 3）と、
  再順位付けの gRPC 輸送（`GrpcRerankCompletionClient`・`ClaudeSearchReranker`。IADR-0498 決定 5・7）。
- **製品コードは変えない**（試験と試験仕様書だけ）。新しい実装判断は無いので IADR は起こさない。

## 受け入れ基準と試験の写像

| # | 基準 | 試験（`PrivateNoteMaintenanceInitialRunTests` / `RerankCompletionTransportTests`） | 殺す変異 |
| --- | --- | --- | --- |
| 1 | 鍵を `"abc"` にしたホストは `StartAsync` で例外になる。陽性対照: `"120"` のホストの保守は 120 秒の前倒しを持つ | `鍵を不正な値にしたホストは起動で止まる` / `鍵を設定したホストの保守は前倒しを持つ` | M4（`Program.cs` の `InitialRunDelay = ResolveInitialRunDelay(...)` を外す） |
| 2 | 偽の時計で「前倒しの間隔 + 24 時間」進めると 2 回目が走る。24 時間に 1 tick 足りなければ走らない | `前倒しの後も24時間ごとの周期へ進む` | M5（前倒しの 1 回の後に周期のループへ進まない） |
| 3 | 前倒しの待ちの最中に停止を要求すると、時計を進めなくても速やかに終わり、周期は走らない | `前倒しの待ちの最中に停止を要求すると速やかに終わる` | M6（前倒しの `Task.Delay` が `stoppingToken` を無視する） |
| 4 | `実チャネルで取り消しと期限を区別する` の不安定さの原因を特定し、時刻に依存しない形に直す（外さない・飛ばさない） | `実チャネルで取り消しと期限を区別する`（書き換え） | 段が利用者の取り消しを縮退へ畳む変異・輸送が取り消しを写さない変異 |

周期の回数は、周期ごとに 1 つ作られるスコープの数で数える（`RunCycleAsync` は周期ごとに `CreateScope` を 1 回呼ぶ）。中身は試験ホストの本物のスコープである。

## 不安定さの根本原因（#4）

- CI の失敗（59187ce3・run 38003999531・job `backend-build (knowledge 2/3)`）は **153 行＝最後の段（利用者の取り消し）の `ThrowAsync<OperationCanceledException>`** で、
  文言は「**no exception was thrown**」、試験の所要は **3 秒**（手元の通常は 1.7〜1.8 秒）。
- 直す前の最後の段は、利用者の取り消し（`CancelAfter(200 ms)`）と段の期限（`TimeoutSeconds = 1`）という **2 つの壁時計の競争**だった。
  段は `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` で「利用者の ct が立っていない取り消し＝期限」と読む。
  CI の停止（ランナーの一時停止・スレッドプールの枯渇）で 2 つのタイマーが同じ巡回で満期になると、段の期限の取り消しが先に観測され、
  段は `timeout` の縮退で**正常に返る** —— 「例外が投げられなかった」になる。
- 再現: 利用者側を `CancelAfter(1500 ms)`（段の期限より後）にすると、手元で同じ文言・同じ行で落ちる（所要 2.76 秒）。
  余裕 800 ms は CI の停止で消える（失敗した回の所要が通常より 1 秒超長いことと整合する）。
- 時間依存以外の候補の確認: ポートは動的（`127.0.0.1:0`）で試験ごとに別のサーバー、共有状態は試験内の計器だけ（`TestMeterFactory` で分離）、
  並行は無い（逐次）。いずれも「例外が無い」を生まない。

## 直し方（#4）

- 受け口の偽物（`HangingCompletion`）が**要求の到着を知らせる**（`WaitArrivalAsync`）。試験は到着を見てから**明示的に**取り消す（時刻で見計らわない）。
- **取り消しの源を回ごとに 1 つにする。** 利用者の取り消しを測る回は段の期限を上限（30 秒）にし、期限が先に立つ余地を構造的に無くす。
  段の期限を測る回は、期限（1 秒）のほかに取り消しの源を持たない（呼び出し元の ct は試験の打ち切りだけ）。
- 段の期限そのもの（1 秒）は実時間のまま残す: 競う相手がいないので順序に依存しない（`ClaudeSearchReranker` は時計を注入できず、そのために製品コードを変えない）。

## 母集合（規則 9）

- 誤りの側の形「`CancelAfter(TimeSpan.FromMilliseconds(...))` と別の期限の競争」を `src/knowledge/backend` の試験で走査した（12 件）。
  本件以外は、競う期限が 30 秒（`DocumentDeletedTimeoutTests`・`CatalogTimeoutTests`・`WikiSyncTimeoutTests`・`IngestionTimeoutTests`・`GraphSyncTimeoutTests`。余裕 150 倍）か、
  競う期限を持たない（`Grpc*Tests` の 50 ms。取り消しの源が 1 つ）。**本件だけが 5 倍の余裕で 2 つの壁時計を競わせていた。** 除外理由は以上。

## 検証

- `RerankCompletionTransportTests` 全 8 件を 30 回連続で実行して 30 回合格。CPU 1 個に 6 本の負荷を重ねた条件でも同じ試験を 30 回実行して 30 回合格。
- 変異（手元で入れて赤を確かめ、戻した）: M4・M5・M6・段の `when (!ct.IsCancellationRequested)` を外す変異。結果は PR 本文に記す。
