---
title: 作業仕様書 — integration-stack の取り込みが起動しない（#2149 の後、コレクションを足す opt-in で受け口の期限の検査が起動を止める）
type: spec
status: done
related_ids: [FR-02, UC-04, ADR-0027, ADR-0016, ADR-0127, IADR-0613, IADR-0595, IADR-0478, IADR-0313, IADR-0085, IADR-0497]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行／デッドレター）
issue: "#2066"
---

# 作業仕様書 — 取り込みの総枠の既定をコレクション数に合わせる（integration-stack の赤の是正）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。判断の記録は **IADR-0613**（新規）に置く。
> 起点は自動起票の #2066（develop の integration-stack が失敗）。

## 事象と根拠（CI のログ）

- develop の integration-stack は d8b97e98（run 39889975278）まで成功し、**次の push da59f651（#2149）の run 39895327907 から 6 回以上連続で失敗**した。
  間のコミットは da59f651 の 1 件だけである（`git log d8b97e98..da59f651 --first-parent`）。
- 門「Gate — the stack is actually up」の出力:
  `[G1] microservices-platform/ingestion-service: availableReplicas=0 < spec.replicas=1` と
  `[G13] Qdrant にコレクションが 1 件も無い`（取り込みが起動しないのでコレクションが作られない。G13 は G1 の従属）。
- 取り込みの Pod は `1/2 Error`（再起動 5 回）／最新の run 39986623480 では `CrashLoopBackOff`（8 回）。コンテナのログ:
  `Unhandled exception. System.InvalidOperationException: 受け口 ingest の実行期限（Ingestion:HandlerTimeoutSeconds＝435 秒）は、外への呼び出しの最悪の所要時間 450 秒（既存チャンクの削除（3 コレクション × …）（30 秒） ＋ 版の印の照会・記録・読み直し（30 秒） ＋ 旧版の点の削除（3 コレクション × …）（30 秒） ＋ 本文の取得（20 秒） ＋ 埋め込みの総枠（300 秒） ＋ 最後の 1 チャンクの埋め込み（30 秒） ＋ 最後の 1 チャンクの登録（10 秒））より長くなければならない。`
  （`IngestionTimeouts.From` ← `Program.cs:75`）。

## 原因

- #2149（IADR-0595 決定 5）が最悪の所要時間に「版の印 3 回」と「書き終えた後の旧版の削除（コレクション数 × Qdrant）」を足し、
  既定の 2 本（voyage ＋ 語彙索引）で 430 秒になるので受け口の期限の既定を 420 → 435 秒へ上げた。
- しかし削除が 2 回になったため、**コレクション 1 本あたり 20 秒**増える。chart の opt-in はコレクションを足す:
  決定的ローカル埋め込み（`embedding.deterministicLocal.enabled`。integration-stack の `LOCALEMBED=1`）と
  Ruri（`embedding.enabled`）。それぞれ 3 本で 450 秒・両方で 4 本 470 秒になり、435 秒に収まらない。
- 受け口の期限は再試行の連鎖の検査（4 × 期限 ＋ 42 ＜ 1800）から **439 秒より上げられない**ので、
  総枠を既定（300 秒）のままにすると、**期限をどう構成しても起動できない**。#2149 は試験の行を (6 本, 既定) → (3 本, 既定) へ書き換え、
  「既定のまま 3 本は止まる」を正として固定していた（opt-in の構成を母集合に入れていなかった）。

## 方式（IADR-0613）

- **総枠を構成で与えていなければ**、総枠の既定を「受け口の期限 − 総枠以外の項目 − 余白 5 秒」へ縮める（上限 300 秒・下限 1 秒）。
  余白 5 秒は既定の構成（2 本）での余り 435 − 430 と同じ値で、**既定の 2 本では 300 秒のまま**（挙動は 1 ビットも変わらない）。
  3 本で 280 秒・4 本で 260 秒。
- 総枠を構成で与えたときは縮めない（運用者の値を黙って変えない）。収まらなければ従前どおり起動を止める。
- 退けた案: (1) 既定の総枠を全体で 260 秒へ下げる（既定の 2 本の配備まで収まる文書を狭める・警報文言など多数の追随）。
  (2) chart で opt-in のときだけ `Ingestion__EmbeddingBudgetSeconds` を描く（式を helm へ複写する）。
  (3) 受け口の期限を上げる（連鎖の検査で 439 秒が上限）。(4) ブローカの `consumer_timeout` を上げる（共有ブローカの全利用者に効く・作り直しが要る）。
  (5) 先に消した場合に書き終えた後の削除を省く（並行の取り込みの防御〔IADR-0595 決定 4〕を緩める）。

## 母集合（規則 9・10）

- 誤りの側の文字列 `3 本なら 450 秒で止まり` / `DefaultEmbeddingBudgetSeconds` / `EmbeddingBudgetKey` / `435` で src・docs・deploy を走査した。
  追随: `IngestionTimeouts.cs`（本体と注記）、`IngestionTimeoutTests.cs`（T-21 の行の意味が変わる・T-21b を追加）、`docs/tests/FR-02_ingestion.md`（T-21 の行）、
  `docs/operations/operations.md`（総枠の既定の注記）。
- 除外: 警報の説明文（`deploy/prometheus/alerts.yml` ほか 5 か所）の「既定 300 秒」は既定の構成では正しいまま（縮むのは opt-in のときだけ）なので変えない。
  `Program.cs` の注記（既定 435 秒）は変わらない。

## 受け入れ基準

1. 既定の構成（2 本）の値は従前どおり（総枠 300・受け口 435）→ T-21（`構成が無ければ既定の上限になる`）・配線の試験が緑。
2. 総枠を与えていなければ 3 本・4 本でも起動できる（総枠 280・260、余り 5 秒）→ T-21b。コードの変更を戻すと赤になることを確かめた（変異）。
3. 総枠を構成で与えれば縮めず、収まらなければ起動を止める（3 本・総枠 300 → 止まる）→ T-21。
4. develop へ入った後の integration-stack で取り込みが Ready になり、門（G1・G13）が通る（PR では workflow_dispatch で確かめる）。

## PoC 配備（MSP#2175、基準 1ef87471）への影響

- 1ef87471 は da59f651 を含む。PoC の取り込みが落ちるのは**コレクションが 3 本以上**（`embedding.enabled=true`〔Ruri〕か `LOCALEMBED=1`）のとき、
  または取り込みの時間の上限を構成で上書きして式に収まらないとき。既定の values（`values-local.yaml` は両方とも無効）なら 2 本で 430 ＜ 435 となり起動する。
