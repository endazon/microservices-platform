---
title: 作業仕様書 — 検索 p95 の警報を、再順位付けの有無（Rerank__Enabled）に合わせて切り替える（#1746 段 S5 の前提・計画 ADR-0129 決定 2 / フォローアップ 3）
type: spec
status: done
related_ids: [FR-03, NFR-01, NFR-21, NFR-29, ADR-0006, ADR-0076, ADR-0127, ADR-0129, IADR-0165, IADR-0345, IADR-0370, IADR-0498, IADR-0559]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0129_rerank-latency-target-nfr29-and-staged-rollout.md（決定 2・3・4、フォローアップ 3）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-01・NFR-29）
---

# 作業仕様書: 検索 p95 の警報を再順位付けの有無で切り替える（#1746 段 S5 の前提）

> 本仕様書は着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `d3c762b4`。

## 起点

- #1746 の 2026-10-07 のコメントの表「フォローアップ 3（警報の切り替え。決定 2）→ S5 の前提に追加」。
  `deploy/prometheus/alerts.yml` の `SearchLatencyP95High` は 1.5 秒の固定である。再順位付けが有効な構成では
  NFR-29 の値で鳴るよう、構成で閾値を切り替えられる仕組みを S5 の前に置く（Grafana・経路 B の写しも同じ）。
- 計画 ADR-0129（Accepted・2026-10-06。2026-10-10 補完）:
  - 決定 2: 無効な構成では NFR-01 の 1.5 秒で鳴らす。有効な構成では NFR-29 の値で鳴らす。NFR-29 が pending の間は
    決定 3 により有効にしないので、警報は 1.5 秒のままである。
  - 決定 3: 有効にする条件は「段 S3 の計測が済んでいる」かつ「NFR-29 の値が利用者裁定で定まっている」。
  - 決定 4 の表: 「警報は構成に合う目標で鳴る」の現在の実現手段は**無い**（1.5 秒の固定）。
- NFR-29（計画 `01_requirements.md`）: **［pending・2026-10-06］検索 p95・RAG 初回応答 p95 とも未定**。
  計画リポジトリ `origin/main` `55e464a`（2026-10-17）で確かめた。**値は無い。実装側で決めない。**

## 着手前の調査（母集合。規則 9・10）

- `git grep -n SearchLatencyP95High`（全文書）: 定義は 4 か所だけ。
  - `deploy/prometheus/alerts.yml`（経路 A の Prometheus）
  - `deploy/local/observability/prometheus.yaml`（経路 B の Prometheus inline）
  - `deploy/grafana/provisioning/alerting/slo-alerts.yaml`（経路 A の Grafana 暫定）
  - `deploy/local/observability/grafana.yaml`（経路 B の Grafana inline）
  - 他は記述（凍結記録の `.ai-context/`・`perf/k6/README.md`・各 description の「SearchLatencyP95High は空ベクタになり…」）。
    閾値・式を持つ箇所は上の 4 か所だけ。helm（`deploy/helm/`）はルールを持たない（`git grep -n "alert:" deploy/helm` が 0 件）。
- 運用仕様書 `docs/operations/operations.md` の警報表「検索レイテンシ」の行（閾値 > 1.5 秒が 10 分）と、再順位付けの節
  「有効化は #1746 の段 S5 で扱う」。
- 同期を守る既存の検査器:
  - `scripts/check-prometheus-alerts-parity.js`: 経路 A/B の Prometheus の群名・ルール名・expr・for・severity の 1 対 1。
  - `scripts/check-grafana-alerting.js`: Prometheus と Grafana のルール名の 1 対 1・件数・uid（40 字以下）・compose と k8s の inline の同内容・
    検査 6（式の絞り込みの後の値で評価器が真になり得る。読めない式は報告）。
  - `scripts/check-grafana-provisioning-parity.js`: Grafana の provisioning の経路 A/B の同内容。
  → いずれも**ルールの追加・式の変更に自動で追随する**（名前・式を読む）。検査器そのものの改修は要らない見込み。
  件数の導出値（Grafana 24 件・platform-slo 4 件）は文書側にあり、計算し直す（規則 10）。
- 再順位付けの状態が Prometheus から読めるか:
  - `Rerank:Enabled` は `Program.cs` が起動時に読む（`SearchRerankOptions`）。**状態を表す計器は無い。**
  - 🔴 **RetrievalService は自分の Meter（`microservices-platform.retrieval-service`）を OTLP の収集対象へ入れていない。**
    `git grep -n AddMeter -- src` で RetrievalService の行が 0 件（他のサービスは `AddOpenTelemetry().WithMetrics(m => m.AddMeter(...))` を持つ）。
    共通の `AddPlatformObservability` が入れるのは `ConsumerTimeoutMetrics` の Meter だけである。
    → 既存の `search.rerank.total`・`search.keyword_degraded.total`・`SearchDegradationMetrics` も Prometheus へ届いていない。
    本件の計器を届けるには `AddMeter` が要るので、同じ変更で直す（運用仕様書の監視の記述が初めて成立する）。
- 名前の写し方の先例: `documents.content_abac.gate.open`（ObservableGauge・単位 `{gate}`）→ Prometheus で `documents_content_abac_gate_open`。

## 方式（IADR-0559）

1. **RetrievalService が再順位付けの構成をゲージで出す**: `search.rerank.enabled`（1 = 有効・0 = 無効。Prometheus では
   `search_rerank_enabled{job="microservices-platform.retrieval-service"}`）。起動時に計器を作る（最初の検索を待たない）。
   `AddMeter(KeywordSearchMetrics.MeterName)` を足す。
2. **`SearchLatencyP95High`（1.5 秒）は再順位付けが有効と報告されている間は鳴らない**:
   `… > 1.5 unless on () (max(search_rerank_enabled{job=…}) == 1)`。ゲージが無い（旧版・収集断）ときは既定（無効）として扱い、
   1.5 秒で鳴る側へ倒す。
3. **有効な構成の閾値（NFR-29）は pending なので値を置かない。代わりに `SearchRerankLatencyTargetUndefined` を置く**:
   `max(search_rerank_enabled{job=…}) == 1` が 5 分続くと warning。「再順位付けが有効だが NFR-29 の検索 p95 目標が未確定
   （ADR-0129 決定 3 の条件を満たしていない）」を鳴らす。黙って無監視にしない。
   NFR-29 が定まったら、このルールを `SearchLatencyP95HighRerankEnabled`（`… > <NFR-29> and on () (max(...) == 1)`）へ置き換える（4 か所）。
4. Grafana 版は閾値を評価器に持つ。`SearchLatencyP95High` の A に同じ `unless` を足す（評価器は `gt 1.5` のまま）。
   新ルールは `(max(search_rerank_enabled{…}) or on () vector(0))` を `gt 0` で比べる（#1577 の形を避ける。無いときは 0 で Normal）。
5. 対象外: RAG 初回応答（`RagFirstTokenP95High`・NFR-02 5 秒）と応答完了（`RagLatencyP95High`）。NFR-29 は RAG 初回応答 p95 も持つが、
   #1746 の表と本件の指示は検索 p95 である。残余として IADR に書く（有効化した時点で `SearchRerankLatencyTargetUndefined` が鳴るので、
   有効化そのものは見逃されない）。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | 既定（無効）でゲージ `search.rerank.enabled` が 0 を出し、OTel の収集対象に入っている | RetrievalService の結合試験（器を起こし、OTel の MeterProvider に試験用の読み手を足して ForceFlush。計器名と値 0） |
| AC2 | `Rerank:Enabled=true` でゲージが 1 | 同上（有効の器） |
| AC3 | `AddMeter` を外すと AC1 が赤になる | 変異（`AddMeter` の行を外して試験を回す） |
| AC4 | 4 か所が同期している（経路 A/B・Prometheus/Grafana） | `check-prometheus-alerts-parity.js`・`check-grafana-alerting.js`・`check-grafana-provisioning-parity.js` |
| AC5 | Prometheus がルールを受理し、式が意図どおり切り替わる | `promtool check rules`・`promtool test rules`（無効・ゲージ無し・有効の 3 通り。手元に promtool があれば） |
| AC6 | 件数の導出値（Grafana 24 → 25・platform-slo 4 → 5）を計算し直して書き換える | 目視＋`git grep` |
| AC7 | 運用仕様書の警報表と再順位付けの節が切り替えを記述する | 文書系検査（trace ブロック等） |

## 検証コマンド（予定）

- `dotnet build` / `dotnet test`（RetrievalService.Tests のみ）・`dotnet format --verify-no-changes`（knowledge の slnx）
- `node scripts/check-prometheus-alerts-parity.js` / `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 文書系: `node scripts/check-trace-blocks.js` ほか CI の文書系ジョブ相当
- promtool（あれば）

## 経過

- 実装: `RerankStateMetrics`（ゲージ）・`Program.cs`（`AddMeter`・登録・起動時の解決）・4 か所のルール（`SearchLatencyP95High` の `unless`、
  `SearchRerankLatencyTargetUndefined` の新設）・件数の導出値（Grafana 24 → 25・platform-slo 4 → 5。`awk` で群ごとに数え直した）・運用仕様書の警報表と再順位付けの節。
- AC1・AC2: `dotnet test Tests/RetrievalService.Tests.csproj` → 573 件 合格（新規 2 件を含む）。
  - 試験の初版は `MeterProvider.ForceFlush()` を使い偽で落ちた（器の OTLP の出口 localhost:4317 が届かない）。足した読み手だけを `Collect()` する形に改めた。
- AC3（変異）: `AddMeter` を外す → 新規 2 件が赤。起動時の解決（`GetRequiredService<RerankStateMetrics>()`）を外す → 2 件が赤。戻して緑。
- AC4: 3 本の検査器が OK（Prometheus 25 / 25・Grafana 25 / 25・provisioning 5 / 5）。変異（k8s の inline の式だけ `max` → `min`）で
  `check-prometheus-alerts-parity.js`（Prometheus の inline）・`check-grafana-alerting.js`（Grafana の inline）がそれぞれ赤。
- AC5: promtool v2.52.0（配備の `prom/prometheus:v2.52.0` と同じ版。GitHub のリリースから取得）
  - `promtool check rules deploy/prometheus/alerts.yml` → SUCCESS 25 rules。経路 B の inline を取り出して同じく SUCCESS 25 rules。
  - `promtool test rules`（両経路のルールで同じ 5 通り）→ SUCCESS: ①ゲージ 0・遅い → 1.5s が鳴り未確定は鳴らない ②ゲージ無し・遅い → 1.5s が鳴る
    ③ゲージ 1・遅い → 1.5s は鳴らず未確定が鳴る ④器 2 つ（1 と 0）→ ③と同じ ⑤ゲージ 0・速い → どちらも鳴らない。
  - 変異（compose の `unless` 行を消す）→ ③④が FAILED。試験の定義はリポジトリに置いていない（CI に promtool が無い。IADR-0559 §残るもの）。
- AC6・AC7: `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 1114 件 合格（初回は IADR 索引のタイトルが 200 字超で赤 → 縮めた）。
  `check-trace-blocks`・`check-trace-followthrough`・`check-test-traceability`・`gen-knowledge-graph --check` → OK。
  CI の `node scripts/*.js` 71 本を手元で流し、赤は kubectl / helm / 稼働クラスタ / ビルド成果物 / GitHub の環境変数が無いものだけ（本変更と無関係）。
- `dotnet format Tests/RetrievalService.Tests.csproj --verify-no-changes` → 0。
- helm は触っていない（`deploy/helm` にルールは無い）。
