---
title: IADR-0559 検索 p95 の警報は、検索サービスが出す再順位付けの構成のゲージ（search_rerank_enabled）で閾値を選ぶ。無効（とゲージが無いとき）は 1.5 秒、有効は NFR-29 が pending のため閾値を置かず「有効時の目標が未確定」を鳴らす
type: impl-adr
status: Accepted
related_ids: [FR-03, NFR-01, NFR-02, NFR-21, NFR-29, ADR-0006, ADR-0076, ADR-0127, ADR-0129, IADR-0165, IADR-0345, IADR-0370, IADR-0498]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0129_rerank-latency-target-nfr29-and-staged-rollout.md（決定 2・3・4、フォローアップ 3）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-01・NFR-29）
related_specs:
  - ../specs/20261017_1746_rerank-aware-search-latency-alert.md
---

# IADR-0559: 検索 p95 の警報は再順位付けの構成のゲージで閾値を選ぶ（#1746 段 S5 の前提）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-17
- 決定者: claude（調整役の依頼。#1746 の 2026-10-07 のコメントの表「フォローアップ 3 → S5 の前提に追加」）

## 起点・関連

- 起点 issue: #1746（段 S5 の前提）。計画 ADR-0129 フォローアップ 3（決定 2 の実装）。
- 関連する計画書 ID: NFR-01（検索 p95 1.5 秒）・NFR-29（再順位付けが有効な場合のレイテンシ。**pending**）・NFR-21（障害検出）。
- 関連する計画 ADR: ADR-0129 決定 2（警報は配備の構成に合う目標で鳴らす）・決定 3（有効化の条件）・決定 4（3 点セット）・
  ADR-0127 決定 3・ADR-0076（SLO の評価対象と単位）・ADR-0006（Alertmanager）。
- 前提とする実装判断: [IADR-0498](./IADR-0498_claude-rerank-stage-at-search-exit.md) 決定 11（既定無効。`Rerank:Enabled`）・
  [IADR-0345](./IADR-0345_slo-alert-metric-alignment.md)（メトリクス名・単位）・[IADR-0370](./IADR-0370_slo-evaluation-target-absent-rules.md)（評価対象の不在）・
  [IADR-0165](./IADR-0165_grafana-interim-alerting.md)（Grafana の暫定）。
- 採番: `origin/develop` の最大は IADR-0558。開いている PR に新しい IADR は無い（2026-10-17 着手時）。本件は **0559**。

## コンテキストと課題

- `SearchLatencyP95High` は 1.5 秒の固定で、4 か所（経路 A/B の Prometheus・Grafana）にある。ADR-0129 決定 4 の表は
  「警報は構成に合う目標で鳴る」の現在の実現手段を「無い」と記録している。
- 再順位付けの有無（`Rerank:Enabled`）は検索サービスが起動時に読むだけで、Prometheus からは見えない。
- 有効時の目標 NFR-29 は **pending**（計画 `01_requirements.md`。2026-10-17 に計画 `origin/main` `55e464a` で確かめた）。
- 🔴 **調査の途中で、RetrievalService が自分の Meter を OTLP の収集対象へ入れていないことが分かった**（`AddMeter` が無い）。
  既存の `search.rerank.total`・`search.keyword_degraded.total`・`SearchDegradationMetrics` は Prometheus へ 1 件も届いていなかった。
  運用仕様書の監視（`search_rerank_total{search_rerank_result="degraded"}` を見る）は成立していなかった。

## 検討した選択肢

### 構成をルールへ伝える手段

1. **サービスが構成をゲージで出し、ルールがそれで分岐する**（**採用**）— 構成の正は `Rerank__Enabled` 1 つのままで、ルールが自動で追随する。
   片方だけ直して食い違う（有効にしたのに 1.5 秒で鳴り続ける・無効に戻したのに外れたまま）余地が無い。
2. 配備の変数（compose の環境変数・helm の値）でルールファイルを切り替える — ルールが 2 組になり、経路 B は kustomize の inline なので
   切り替えの口が無い。サービスの構成と警報の構成を別々に書き換える運用になる。
3. 閾値もサービスの構成に持たせ、ゲージで出してルールが比べる（`p95 > on() group_left target`）— NFR の値がルールから読めなくなり、
   値の正が構成へ散る。NFR-29 が pending の今は出す値も無い。

### NFR-29 が pending の間、有効な構成で何を鳴らすか

1. **閾値つきの警報を置かず、「有効と報告されていること」を鳴らす（`SearchRerankLatencyTargetUndefined`）**（**採用**）
   — 有効にする条件（決定 3）の外にいることそのものを知らせる。1.5 秒の警報は外れるが、黙って無監視にはならない。
2. 有効でも 1.5 秒で鳴らし続ける — 決定 2 に反する。ADR-0129 が却下した選択肢（「有効にすると鳴り続ける」）そのもの。
3. 仮の値（例: 期限 8 秒 ＋ 1.5 秒）で鳴らす — 値を実装で決めることになる（決定 1・ADR-0129 §理由「根拠の無い数字になる」）。
4. 有効なら何も鳴らさない — 条件の外の有効化が見えなくなる。

## 決定

### 決定 1: 検索サービスが構成をゲージ `search.rerank.enabled` で出す

- `RerankStateMetrics`（`Common/Observability/RerankStateMetrics.cs`）。値は 1 = 有効・0 = 無効。単位 `{state}`。Prometheus では
  `search_rerank_enabled{job="microservices-platform.retrieval-service"}`（先例 `documents_content_abac_gate_open` と同じ写し方）。
- **起動時に作る**（`Program.cs` が Build の直後に解決する）。最初の検索を待つと、その間ルールが「無効」と読む。
- **`AddMeter(KeywordSearchMetrics.MeterName)` を足す**（他のサービスと同じ `AddOpenTelemetry().WithMetrics(...)` の形）。
  既存の検索の計器もこれで初めて届く（コンテキストの 🔴）。

### 決定 2: 1.5 秒の警報は、有効と報告されている間だけ外す

- Prometheus: `<p95> > 1.5 unless on () (max(search_rerank_enabled{job=…}) == 1)`。
- **ゲージが無い（旧版・収集断・起動前）ときは既定の無効として扱い、1.5 秒で鳴る側へ倒す**（`unless` の右辺が空なら外さない）。
  既定は無効であり（IADR-0498 決定 11・ADR-0129 決定 3）、ゲージが読めないことを「有効」と読むと既定状態の監視が消える。
- 複数の器が混ざる（入れ替えの途中）ときは `max` で有効側へ倒す（決定 3 の警報も同時に鳴る）。
- Grafana 版は閾値を評価器に持つので、A の式に同じ `unless` を足し、評価器は `gt 1.5` のまま。外れている間 A は空になり
  `noDataState: NoData` で NoData になる（無風の時と同じ見え方。Grafana の暫定には宛先が無い〔IADR-0165〕）。

### 決定 3: 有効な構成では、目標が未確定であることを鳴らす（NFR-29 が定まるまで）

- `SearchRerankLatencyTargetUndefined`（群 `platform-slo`・warning・`for: 5m`）: `max(search_rerank_enabled{job=…}) == 1`。
- Grafana 版は #1577 の形（絞ってから `gt 0`）を避け、`max(…) or on () vector(0)` を `gt 0` で比べる（ゲージが無いときは 0 として Normal）。
- **NFR-29 が定まったら、このルールを `SearchLatencyP95HighRerankEnabled`（`<p95> > <NFR-29 の秒数> and on () (max(search_rerank_enabled{…}) == 1)`、
  Grafana は同じ式の A と `gt <秒数>`）へ置き換える。** 4 か所を同時に直す（下の検査器が名前・式・同内容を止める）。
- 本決定で ADR-0129 決定 4 の表「警報は構成に合う目標で鳴る」は、**切り替えの仕組みは「ある」・有効側の閾値は「NFR-29 待ち」**になる。

### 決定 4: 同期は既存の検査器で守る（検査器は改修しない）

- `check-prometheus-alerts-parity.js`（経路 A/B の Prometheus の群名・名前・expr・for・severity）・`check-grafana-alerting.js`
  （名前の 1 対 1・件数・uid・k8s の inline の同内容・検査 6）・`check-grafana-provisioning-parity.js` は、ルールの追加と式の変更に自動で追随する。
  変異（k8s の inline の式だけを変える）で、前者 2 本がそれぞれ赤になることを確かめた。

## 理由

- 決定 2 の「配備の構成に合う目標」を、構成の正を 1 つに保ったまま満たせるのは、サービス自身の報告だけである（選択肢 1）。
- NFR-29 の値は計画が段 S3 の実測を見て定める（ADR-0129 決定 1）。実装が値を置くと、計画の pending を実装で埋めることになる。
- 有効化は条件が揃ってから行う（決定 3）。条件の外で有効になったことは、閾値が無くても機械で分かる。それを鳴らすのが安全側である。

## 結果

- **良い影響**
  - `Rerank__Enabled` を切り替えるだけで、警報が構成に合う側へ切り替わる（ルールの書き換えは要らない）。
  - 検索サービスの計器（再順位付けの結果・全文の縮退・応答の縮退の印）が初めて Prometheus へ届く。
- **悪い影響 / トレードオフ**
  - 有効な構成では、NFR-29 が定まるまで検索 p95 の閾値つきの警報が無い（未確定の警報が鳴り続ける）。決定 3 の条件の外の状態なので受け入れる。
  - Grafana の暫定では、有効な構成で `SearchLatencyP95High` が NoData になる。
- **残るもの**
  - 🔴 **RAG 初回応答（`RagFirstTokenP95High`・NFR-02 5 秒）と応答完了（`RagLatencyP95High`）は切り替えていない。** NFR-29 は RAG 初回応答 p95 も持つ
    （決定 2 の射程に入る）が、#1746 の表と本件の依頼は検索 p95 である。有効にすると 5 秒で鳴り得るが、そのときは本 IADR 決定 3 の警報も鳴るので、
    条件の外の有効化は見逃されない。NFR-29 が定まるときに、同じゲージで RAG の 2 本も切り替える（ゲージは AiAnalysisService の job を跨いで `on ()` で引ける）。
  - Prometheus がこの式を評価して切り替わることは `promtool test rules`（v2.52.0。配備と同じ版）で 5 通り（無効・ゲージ無し・有効・入れ替え中・速い）を確かめた。
    **稼働クラスタでゲージの系列が実在することは未確認**（IADR-0345 の手順で `/api/v1/series` に `search_rerank_enabled` を引き、陽性対照を対で置くこと）。
    promtool の試験は CI に無い（promtool を持たない）ので、作業仕様書に記録するだけで、リポジトリには置かない。
