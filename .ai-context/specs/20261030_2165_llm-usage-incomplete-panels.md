---
title: 作業仕様書 — llm-usage ダッシュボードに不完全な計上と逐次応答の取り消しのパネルを足す（#2165）
type: spec
status: done
related_ids: [FR-11, NFR-21, ADR-0044, IADR-0601, IADR-0164]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2165"
---

# 作業仕様書 — llm-usage の不完全な計上・取り消しのパネル（#2165）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `5a9cee5c`）。起点は #2116（IADR-0601）で射程外とした残り。

## 現状（実測）

- 計器は実装済み: `llm.usage.incomplete.total`（属性 `llm.incomplete_reason` / `llm.usage_coverage`。`LlmUsageMetrics.cs`）、
  `llm.completion.total{llm.result="cancelled"}`（`LlmCompletionMetrics.ResultCancelled`）。
- 可観測性仕様書 `docs/observability/llm-usage-and-cost-metrics.md` §クエリ例に不完全な計上の PromQL はあるが、取り消しの PromQL は無い。
- ダッシュボード `llm-usage` の定義は 2 か所（`deploy/grafana/provisioning/dashboards/llm-usage.json` と `deploy/local/observability/grafana.yaml` の ConfigMap の同内容）。
  一致は `scripts/check-grafana-provisioning-parity.js` が見る。名前の実在は `scripts.repo.test.js` の「llm-usage ダッシュボード: 式の名前がすべて実装に実在する」が見る（属性名の列挙に `incomplete_reason`・`usage_coverage` が無い）。

## 方針

1. 単価の未解決のパネル（id 20）の下に 1 段足す（以下の段は 6 下げる）。
   - id 25「逐次応答の途中終了（不完全な計上）」: `sum by (llm_purpose, llm_incomplete_reason, llm_usage_coverage) (increase(llm_usage_incomplete_total[$__range]))`（仕様書の式そのまま）。
   - id 26「逐次応答の取り消し」: `sum by (llm_purpose) (increase(llm_completion_total{llm_result="cancelled"}[$__range]))`。仕様書 §クエリ例にも同じ式を足す。
2. 費用のパネル（id 21・22）の説明と、冒頭の READ ME FIRST に「不完全な計上が 0 でない期間の金額は下限」と書く。
3. `version` を 2 → 3。
4. k8s の ConfigMap は JSON ファイルを 4 字下げで写す（手で書かない。スクリプトで生成して一致を確かめる）。
5. repo テスト: 属性名の列挙に 2 つを足す。新しいパネルの式が仕様書の PromQL に一字一句あることを固定する。

## 受け入れ基準

- [x] 2 経路のダッシュボード定義がパリティ検査を通る
- [x] 新しいパネルのクエリが仕様書の PromQL と一致する（repo テストで固定）
- [x] 月次費用パネルの注記に「不完全が 0 でない期間は下限」がある

## 母集合（規則 9）

- `llm_usage_incomplete_total` と `cancelled` の読み方を書く文書: 可観測性仕様書（§クエリ例・§ダッシュボード・§監視観点）、月次確認 Runbook（`llm-cost-monthly-review-runbook.md`）。Runbook に「不完全」の読み方が無ければ 1 行足す。

## 残余リスク

- 描画は Grafana を起動して確かめていない（名前の実在と式の一致だけを機械で固定する）。
