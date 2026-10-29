---
title: 作業仕様書 — RAG の初回応答 p95 の警報の窓を 60 分の標本に合わせて広げる（[6h]・for 30m。暫定）（#2334）
type: spec
status: done
related_ids: [NFR-02, ADR-0076, ADR-0079]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2334"
---

# 作業仕様書 — RAG の初回応答 p95 の警報の窓（#2334）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `c09f906d`）。親 #2130（子 A）。子 B は #2335（60 分の SLO 用プローブ）、子 C は #2336（稼働環境での実測）。

## 現状

- `RagFirstTokenP95High` は `rate(...[5m])`・`for: 10m`。SLO 評価用の標本は 60 分に 1 回（計画 ADR-0079 決定 1）なので、標本の無い 55 分間は rate が 0 になり p95 が算出されない（NaN で比較が偽）。遅い標本が来ても 5 分しか条件が立たず、`for: 10m` を満たさないので鳴らない。
- 計画 ADR-0079 決定 3: 窓を標本量に合わせて広げる。具体値は実装が実測で定める。検知遅延（範囲 ＋ `for` ＋ 間隔）の上限は 8 時間。
- 課金の判断は決定 2（Accepted）で済んでいる。60 分のプローブそのものは未配備（#2335）。

## 方針

1. 窓を `[6h]`・`for: 30m` にする。
   - 6 時間には標本がおよそ 6 個入る（p95 は 6 個の上位で決まる。標本が少ないので、遅い標本 1 個で鳴る ——「悪化を拾う」側に倒れる）。
   - 検知遅延は 6h ＋ 30m ＋ 60m ＝ 7.5h ≤ 8h。
   - `for: 30m` は、標本の無い時間帯も値が保たれる（6 時間の窓に残る）ので、30 分の継続で鳴る。1 回の取りこぼしで揺れないようにする。
   - **値は暫定**。稼働環境で p95 が安定して算出されることの実測は #2336。
2. 系列の欠落の警報（`absent_over_time(...[2h])`）は**本 PR では置かない**。60 分のプローブ（#2335）が配備されるまで系列が無いので、今置くと鳴り続ける。#2335 で一緒に入れる（alerts.yml の注記に書いた）。
3. 4 か所の写し（`deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・Grafana の 2 つ）を揃える。Grafana の `relativeTimeRange` は据え置く（式の中の範囲が窓を決める。既存の `absent_over_time(...[2h])` の規則と同じ扱い）。
4. 運用仕様書の表を直し、3 点セット（統制・現在の実現手段・配備までの暫定手段）を足す。可観測性の文書のクエリ例と表を直す。
5. ダッシュボードのパネル（`[5m]` の p95）は変えない（判定ではなく観察の面。人の利用がある時間帯の細かい変化を見る）。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "RagFirstTokenP95High\|rag_answer_first_token_duration_seconds_bucket" -- deploy scripts docs` で走査した。
  - 警報の 4 か所 —— 直す。
  - ダッシュボード 2 か所（`microservices-platform-overview.json`・`grafana.yaml` のパネル）—— 変えない（方針 5）。
  - 文書 3 か所（`docs/observability/rag-first-token-latency.md` のクエリ例と表・`docs/operations/operations.md` の表）—— 直す。`docs/operations/search-rerank-s3-measurement-runbook.md` は計測の手順の式で、判定ではないので変えない。
  - `scripts/check-grafana-alerting.js` は件数の履歴の注記だけ（件数は変わらない）。
- **規則 10**: alerts.yml の「`absent_over_time` は配備後に入れる」の注記は、入れる先を #2335 と書き足した。運用仕様書の「10 分」は 30 分に直した。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 60 分おきの標本がすべて速い → 鳴らない。標本の間でも p95 が算出される | `deploy/prometheus/tests/rag-first-token-window.test.yml` 場面 1 | `[5m]` では標本の間に p95 が算出されない（式の値の試験が赤になる） |
| 遅い標本が 1 回入る → 30 分後に鳴り、6 時間の窓から出れば収まる | 同 場面 2 | `[5m]`・`for: 10m` では鳴らない（赤） |
| 写しが揃う | `check-prometheus-alerts-parity`・`check-grafana-alerting`・`check-grafana-provisioning-parity` | — |

🔴 **手元に promtool が無い**（導入は許可されていない）。試験の赤と緑は CI（`static-checks-units` の `promtool test rules`）で確かめる。修正前の赤は式と標本から導いた（場面 2: 240 分の遅い標本は、`[5m]` では 245 分以降の rate が 0 になり、`for: 10m` に届かない）。
