---
title: 作業仕様書 — SLO アラート 5 件が「発火しうる」状態になるまで直す（#1110）
type: spec
status: draft
related_ids:
  - NFR
  - NFR-21
  - ADR-0006
  - IADR-0130
  - IADR-0164
  - IADR-0165
  - IADR-0304
  - IADR-0322
  - IADR-0346
author: claude
created: 2026-08-31
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (§アラートの通知経路・§ゴールデンシグナルとSLO/SLI)
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md
related_specs:
  - "20260830_issue-1090-546_collector-telemetry-parity-and-alert-delivery.md"
  - "20260830_issue-546_alertmanager.md"
  - "20260810_issue-665_grafana-alerting.md"
issue: "#1110"
---

# 作業仕様書 — #1110

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-21**（運用・保守／障害検出〜復旧。**MTTR 30 分以内・検出 5 分以内**）。
  planning `02_requirements/01_requirements.md` 行 144。
  ［2026-08-08 注記］「アラートルール（SLO ベース）は Prometheus が評価済みであり、要件を満たすのは通知の配線である」
- 計画 ADR: **ADR-0006**（可観測性スタック）。§決定「メトリクスを Prometheus/Grafana … **アラートは Alertmanager を用いる**」
- 計画書: `06_technical/05_observability-ops.md` §アラートの通知経路（2026-08-08 確定）／§ゴールデンシグナルとSLO/SLI
- 実装 issue: **#1110**（出所は #1112 ＝ #1090 / #546 の実測）
- 先行実装 ADR: IADR-0304（Alertmanager 配備・`default-null`）／IADR-0165（Grafana 暫定アラート）／
  IADR-0322（転送構成の自己テレメトリ・検査器の 2 回目判断）

🔴 **計画の前提が 1 つ崩れている。** NFR-21 の注記と ADR-0006 の 2026-08-08 追記はどちらも
**「アラートルールは既に評価されており、欠けているのは通知の配線だけである」**と書いている。
本件の実測はこれを否定する —— **5 件のうち 4 件は評価対象を 1 本も持たない。**
配線を全部やっても、その 4 件は永久に発火しない。**計画へ環流する**（§計画書との差異）。

## 目的・背景

**「アラートが設定されている」ことと「アラートが発火しうる」ことは別である。**
SLO ルール 5 件のうち 4 件が、Prometheus に**一度も存在したことのないメトリクス名**を見ている。
式は構文として正当なので Prometheus はエラーを出さず（`health: "ok"` / `state: "inactive"`）、
Alertmanager を配備しても届く中身が無い。**NFR-21 を担っているのはこの 4 件である。**

## 🔴 母集合（着手時に自分で引き直した。issue 本文の「反映先」は使っていない）

`traceability.repo.md` §是正・追随の母集合の取り方 に従い、**誤りの側の文字列から**、
**拡張子で絞らず**、**パスから**、**軸を複数**引いた。

| 軸 | 検索語（誤りの側） | 実行 |
| --- | --- | --- |
| A | `http_server_duration_milliseconds` | `git grep -lI` |
| B | `service_name` | `git grep -nI -- deploy docs scripts perf src` |
| C | `http_status_code` | `git grep -nI` |
| D | ルール名 5 種（`OtelCollectorDown` 他） | `git grep -lI -e ...` |
| E | 正しい側 `http_server_request_duration` | `git grep -lI`（**追随漏れの陰性確認**） |
| F | `http_server`（接頭辞だけ。名前の書き換わりを取りこぼさない） | `git grep -lI` |
| G | `milliseconds`（単位の側から） | `git grep -nI -- deploy docs` |

### 引いた結果（対象 8 件）

| # | ファイル | 何が誤っているか |
| ---: | --- | --- |
| 1 | `deploy/prometheus/alerts.yml` | 4 ルールの式・注釈・冒頭コメント |
| 2 | `deploy/local/observability/prometheus.yaml` | 同上の inline（経路B） |
| 3 | `deploy/grafana/provisioning/alerting/slo-alerts.yaml` | 同 4 ルールの Grafana 版・しきい値（`params`） |
| 4 | `deploy/local/observability/grafana.yaml` | 3 と 5 の inline（パリティ 2 部） |
| 5 | `deploy/grafana/provisioning/dashboards/microservices-platform-overview.json` | パネル 1〜4 の式・凡例・パネル題（単位 ms） |
| 6 | `docs/operations/operations.md` | §監視・アラート の SLO 表 |
| **7** | **`deploy/alertmanager/alertmanager.yml`** | **`group_by` / `inhibit_rules.equal` が `service_name`** |
| **8** | **`deploy/local/observability/alertmanager.yaml`** | **7 の inline（経路B）** |

🔴 **7・8 は issue 本文の反映先リストに無い。** 軸 B（`service_name`）を引いて出た。
ラベル軸を `job` へ移すと、**Alertmanager 側の束ね（`group_by`）と抑止（`equal`）が
存在しないラベルを指すようになる** —— 束ねが効かず、critical が warning を抑止しなくなる。
**アラート側だけ直すと、この壊れ方を新しく作る。**（規則 10: 是正のたびに、
この変更で新たに誤りになる自分の記述を引き直す。）

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0304_*.md` / `IADR-0322_*.md` / `.ai-context/specs/20260830_*.md`（軸 A・D・E・F） | **凍結記録**。`traceability.repo.md` §Superseded 引用 のとおり本文を後から書き換えない。**しかも当該記述は「誤りの指摘」そのものであり、直すと記録が意味を失う** |
| `docs/migration/rename-knowledge-platform.md`（軸 B） | 語 `service_name` は OTEL のリソース属性名（`service.name`）を指す散文であり、**Prometheus のラベル名ではない**。誤っていない |
| `deploy/local/infra/otel-collector.yaml:71`（軸 D） | `OtelCollectorDown` に触れるコメントだが、**触れているのは fail-safe 構成の帰結**であり、メトリクス名・ラベル・単位を 1 つも含まない |
| `docs/operations/operations.md:671` / `:745`（軸 D） | 前者は #546 の**実測記録**（`OtelCollectorDown` の発火。事実として正しい）、後者は Runbook の**ルール名参照**のみ。式を含まない。**同ファイルの SLO 表（:683〜689）は対象**である |
| `perf/k6/README.md:67-68`（軸 D） | 「`SearchLatencyP95High` / `RagLatencyP95High` / `HighHttp5xxRate` の発火有無で SLO 逸脱を判定できる」。**本 PR まで偽であった記述だが、本 PR で真になる**。式もメトリクス名も含まないため書き換えない（規則 10 の対象は「新たに誤りになる記述」であり、これは逆向きである） |
| `deploy/docker-compose.yml` ほか（`1500`/`5000` の数値一致） | ポート番号・タイムアウト等の別物。**数値の一致は根拠にならない**ので式の文脈があるものだけ採った |

軸 E（正しい側 `http_server_request_duration`）は、**凍結記録 2 件を除くと追跡下に 0 件**であった
—— つまり**正しい名前を書いている稼働資産は 1 つも無い。**「片方だけ既に直っている」は存在しない。
軸 F（`http_server` の接頭辞だけ）は軸 A と**同じ 6 件**へ収束した（名前の変種の取りこぼしが無いことの確認）。

## 実測（着手前・稼働 k3s `platform-infra`）

🔴 **環境の前提を先に潰した。** 稼働 collector は `exporters: [debug]` の fail-safe 構成であり、
**そのままではアプリのメトリクスが Prometheus へ 1 件も届かない**（#1112 が実測した状態のまま）。
`deploy/local/observability/otel-collector-forward.yaml` を apply → `rollout restart` してから測った。
**測り終えたら fail-safe へ戻す。**

🔴 **測定器の欠陥を 1 つ見つけて潰した。** `wget --post-data='query=…'` は
`application/x-www-form-urlencoded` であり、**式中の `+` が空白へ復号される**。
`{__name__=~"otelcol_.+"}` が 0 件で返り、**「存在しない」と読みかけた。**
陽性対照（`/api/v1/series` は同じ系列を 4 本返す）が食い違ったので気づいた。
以後 `+` は `%2B` で送る。**本書の「0 件」は、すべて対になる陽性対照つきである。**

### 5 ルールが参照するメトリクスの実在（陽性対照つき）

| ルール | 参照メトリクス | 実在 | 陽性対照（同じ問い合わせ方） |
| --- | --- | --- | --- |
| `OtelCollectorDown` | `up{job="otel-collector"}` | **1 系列** | 自身が対照 |
| `ServiceRequestMetricsAbsent` | `http_server_duration_milliseconds_count` | **0 系列** | `otelcol_receiver_accepted_metric_points` → **4 系列** |
| `HighHttp5xxRate` | 同上 ＋ ラベル `http_status_code` | **0 系列** | `http_response_status_code` → **90 系列 / 9 値** |
| `SearchLatencyP95High` | `http_server_duration_milliseconds_bucket` | **0 系列** | `http_server_request_duration_seconds_bucket` → **1560 系列** |
| `RagLatencyP95High` | 同上 ＋ `http_route="/analysis/ask"` | **0 系列** | 同上（aianalysis に 45 系列） |

`/api/v1/label/__name__/values` に現れる `http_server*` は **4 つだけ**である ——
`http_server_active_requests` / `http_server_request_duration_seconds_{bucket,count,sum}`。
**`http_server_duration_milliseconds*` は 1 つも無い。**

### ずれの切り分け（名前の誤り / 未実装 / 転送されていない）

🔴 **4 件とも「名前の誤り」である。未実装ではない。**

- 計器は `AddAspNetCoreInstrumentation()`（`ObservabilityExtensions.cs`）で全サービスに入っている。
  現行の OTel HTTP セマンティック規約（安定版）では計器名は **`http.server.request.duration`・単位は秒**である。
  ルールが書いているのは**規約が安定化する前の旧名**（`http.server.duration`・ミリ秒）である。
- 転送されていないのでもない —— 転送を有効にした状態で `http_server_request_duration_seconds_count`
  が **104 系列 / 26 の `job`** 届いている。
- ラベル軸も「名前の誤り」である。`prometheusremotewrite` はリソース属性
  `service.namespace`+`service.name` を **`job`** へ、`service.instance.id` を `instance` へ写す。
  したがってアプリのメトリクスに `service_name` は**付かない**（104 系列中 0 件。
  陽性対照: collector 自己テレメトリは `service_name="otelcol-contrib"` を持つ＝セレクタ自体は効く）。
- 単位も「名前の誤り」に付随する。実測 p95（retrieval）は **0.00475**（秒）。
  しきい値 `> 1500` は秒では 1500 秒＝25 分であり、**構文は正しいまま永久に真にならない。**

→ **したがって #1091 型の「別 issue へ分ける」は不要である。** 計器の実装は要らない。
ただし **`/analysis/ask` は稼働中に一度も呼ばれておらず**（`http_route` の実測値は
`/health/live` / `/health/ready` / `/internal/introspection` の 3 つ）、
**`RagLatencyP95High` の変異試験には人為的な呼び出しが要る**（§変異試験）。

## 対象範囲

- **対象**: 上表 8 ファイル。ルール 4 件の式・単位・注釈、ダッシュボードのパネル 1〜4、
  Alertmanager の束ね／抑止ラベル、運用仕様書の SLO 表、退行防止の判断。
- **対象外**:
  - `OtelCollectorDown`（既に発火しうる。#1112 が実測済み。**触らない**）
  - LLM 予算アラート（**#1111**。前提 3 つが未達で分離済み）
  - ダッシュボードのパネル 5・6（`graph_edge_type_fallback_total` / `ingest_unknown_tag_total`。
    **本件のずれとは別**であり、生産者の有無は別途の問題）
  - Alertmanager の受信先（`default-null`。**実環境の判断**。IADR-0322 が据え置いた）
  - 転送オーバーレイを既定にすること（fail-safe は意図された既定である）

## 設計

### 是正後の式（4 ルール）

| ルール | 式（Prometheus 版） |
| --- | --- |
| `ServiceRequestMetricsAbsent` | `sum by (job) (rate(http_server_request_duration_seconds_count[5m])) == 0 and on (job) (sum by (job) (http_server_request_duration_seconds_count offset 15m) > 0)` |
| `HighHttp5xxRate` | `sum by (job) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])) / sum by (job) (rate(http_server_request_duration_seconds_count[5m])) > 0.05` |
| `SearchLatencyP95High` | `histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{job="microservices-platform.retrieval-service"}[5m]))) > 1.5` |
| `RagLatencyP95High` | `histogram_quantile(0.95, sum by (le) (rate(http_server_request_duration_seconds_bucket{job="microservices-platform.aianalysis-service", http_route="/analysis/ask"}[5m]))) > 5` |

- 注釈の `{{ $labels.service_name }}` を **`{{ $labels.job }}`** へ。
- Grafana 版はしきい値を `data[].model.expr` ではなく **`conditions[].evaluator.params`** に持つので、
  **1500 → 1.5 / 5000 → 5** をそちらで直す（式側に閾値は無い）。
- ダッシュボードのパネル題 **`HTTP P99 Latency (ms)` → `(s)`** / **`RAG Ask Latency P99 (ms)` → `(s)`**。
  **単位の表示を直さないと、0.005 を 5 ミリ秒と読む**（値が 1000 倍ずれて見える）。
- Alertmanager の `group_by` / `equal` を `['alertname', 'job']` へ。

### しきい値を式へ入れるか、`params` へ入れるか

**Prometheus 版は式へ、Grafana 版は `params` へ入れる。既存の分担を変えない。**
`check-grafana-alerting.js` はルール名の 1 対 1 しか見ないので、どちらでも通る。
**通るからこそ、分担を勝手に動かさない**（#665 が選んだ形である）。

### 退行防止（🔴「同型の事故が 2 回起きたら」の判定）

**本件は「ルールの式が参照するメトリクスが実在しない」という事故である。何回目か。**

| 回 | 事故 | 判断 |
| ---: | --- | --- |
| 1 | **本件（#1110）** | —— |

**1 回目である。よって検査器を新設しない**（`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」）。

**IADR-0322 が新設した `check-collector-self-telemetry.js` の同型ではない。**
あちらは「**同じ宣言が複数の配備設定の間で食い違う**」＝ **静的な自己整合**の欠落であり、
リポジトリ内の 3 ファイルを突き合わせれば検出できる。
本件は「**式が参照する名前が、稼働中の TSDB に存在するか**」＝ **リポジトリの外の事実**であり、
**静的検査では原理的に判定できない**（メトリクス名はアプリのコードにも設定にも文字列として現れない
—— OTel の計器名 `http.server.request.duration` は**ライブラリの内部**にあり、
Prometheus 名 `http_server_request_duration_seconds_count` は**exporter の変換規則**が作る）。
CI に Prometheus と全サービスを立てて実データを流す門は、既に `integration-stack.yml` が
**それだけで数分かかる**規模であり、1 回目の事故に対して見合わない。

**代わりに 1 回目として記録に留める**（IADR-0304 が同じ形で「検査器は足さない」と記録し、
2 回目の #1090 で IADR-0322 が足した前例に揃える）。**記録の置き場は 2 つ**にする。

1. **IADR-0346** に「2 回目が起きたら何を作るか」まで書く（次の担当が判断をやり直さずに済む形）。
2. **`alerts.yml` / `slo-alerts.yaml` の冒頭**に、**式の検証手順**（稼働 Prometheus へ
   `/api/v1/series` で問い合わせ、陽性対照を対で置く）を書く。**人が守る手順として明示する。**

## 実測結果（2026-09-02・稼働 k3s `platform-infra`。生出力）

### 受け入れ基準 3: 5 件すべてが評価対象を持つ（陽性）／旧式は持たない（陰性対照）

```
RULE                           FIXED (evaluation target)  OLD (negative control)
OtelCollectorDown              1 series,  sample=1        0 series
ServiceRequestMetricsAbsent    26 series, sample=0.149999 0 series
HighHttp5xxRate                26 series, sample=0.149999 0 series
SearchLatencyP95High           1 series,  sample=0.008499 0 series
RagLatencyP95High              1 series,  sample=0.062844 0 series
```

`/api/v1/label/__name__/values` の `http_server*` は 4 つだけ（陽性対照: 全体で 92 の名前を返す）:

```
"http_server_active_requests"
"http_server_request_duration_seconds_bucket"
"http_server_request_duration_seconds_count"
"http_server_request_duration_seconds_sum"
```

🔴 `RagLatencyP95High` は**着手時 0 系列**であった（`/analysis/ask` が一度も呼ばれていない）。
人為的に POST して `http_route` を生やし、**50 秒後に系列が出現**した。

```
t=  0s routes=['/health/live', '/health/ready', '/internal/introspection']
t= 50s routes=['/analysis/ask', '/health/live', '/health/ready', '/internal/introspection']
APPEARED at t=50s
```

### 受け入れ基準 4: 変異試験（Alertmanager `/api/v2/alerts` への到達）

一時ルール群 `probe-issue-1110` を SIGHUP で読み込ませた（TSDB を失わないため再起動しない）。

```
GROUP probe-issue-1110
   ProbeServiceRequestMetricsAbsent   state=firing    health=ok   alerts=26
   ProbeHighHttp5xxRate               state=firing    health=ok   alerts=24
   ProbeSearchLatencyP95High          state=firing    health=ok   alerts=1
   ProbeRagLatencyP95High             state=firing    health=ok   alerts=1
   ProbeNegativeControlMustNotFire    state=inactive  health=ok   alerts=0
   ProbeOtelCollectorDown             state=firing    health=ok   alerts=1
```

Alertmanager `/api/v2/alerts`:

```
total alerts delivered to Alertmanager: 53
  ProbeHighHttp5xxRate               count=24  status=active   jobs=['ai-stock-trading.audit-service', ...]
  ProbeOtelCollectorDown             count=1   status=active   jobs=['otel-collector']
  ProbeRagLatencyP95High             count=1   status=active
  ProbeSearchLatencyP95High          count=1   status=active
  ProbeServiceRequestMetricsAbsent   count=26  status=active   jobs=['ai-stock-trading.audit-service', ...]

NEGATIVE CONTROL present? False (must be False)
all 5 production rule shapes represented? ['HighHttp5xxRate', 'OtelCollectorDown',
  'RagLatencyP95High', 'SearchLatencyP95High', 'ServiceRequestMetricsAbsent']
```

🔴 **合成プローブの発火は「本番ルールが発火した」ではない。** ただし本番の
`ServiceRequestMetricsAbsent` は**変異なしで `pending`** に入り、実在の `job` を伴った ——
**旧式では `pending` にすら入れなかった**（評価対象 0 のため）。

```
t=240s  ServiceRequestMetricsAbsent -> pending 2 ['microservices-platform.ingestion-service',
                                                 'microservices-platform.conversion-service']
```

### 束ねの軸（`group_by`）—— 対象 7・8 の根拠

同じ 54 件のアラートに対し Alertmanager の設定だけを入れ替えた。

```
group_by: ['alertname', 'job']           -> 54 群（キーの形 (alertname, job)）
group_by: ['alertname', 'service_name']  ->  4 群  🔴 ProbeHighHttp5xxRate の 26 件が 1 群へ潰れた
```

### Grafana provisioning（未決事項を閉じた）

```
=== BEFORE: 5 rules provisioned ===        === AFTER: 5 rules provisioned ===
  OtelCollectorDown            thr=[0]       OtelCollectorDown            thr=[0]
  ServiceRequestMetricsAbsent  OLD-BROKEN    ServiceRequestMetricsAbsent  OK
  HighHttp5xxRate              OLD-BROKEN    HighHttp5xxRate              OK
  SearchLatencyP95High         thr=[1500]    SearchLatencyP95High         thr=[1.5]
  RagLatencyP95High            thr=[5000]    RagLatencyP95High            thr=[5]
```

🔴 **稼働 Grafana には旧式が残っていた**（Prometheus 側だけ直っていた）。すべて `provenance: "file"`。

### 受け入れ基準 8: fail-safe へ戻したこと

```
--- 1. otel-collector: fail-safe (debug only) ---
  prometheusremotewrite occurrences: 0
--- 2. prometheus rules: 本番 5 件 / probe 参照 0 件 ---
  5
  0
--- 3. alertmanager: group_by/equal は job ---
  2:  group_by: ['alertname', 'job']
  12:    equal: ['alertname', 'job']
--- 4. pods ---
  alertmanager-8b47567cd-lvkdz      true   3
  grafana-667dff455d-994kt          true   2
  otel-collector-864f49647f-5kdck   true   0
  prometheus-6bb9574cd4-8kx5q       true   3
```

一時的に張った `/analysis/ask` の port-forward と負荷生成も停止した
（**他エージェントの port-forward（qdrant / document-service）は残置**）。

## 受け入れ基準

- [x] 1. 4 ルールが、実際に届いているメトリクス名・ラベル名・単位を使う（8 ファイル同時）
- [x] 2. ダッシュボードのパネル 1〜4 も同時に直す（**単位表記を含む**）
- [x] 3. **5 件すべてが「発火しうる」ことを稼働クラスタで実測する**
      —— 各ルールの式が**非空のベクタを返す**こと（＝評価対象がある）
- [x] 4. 🔴 **変異試験**: 閾値を割る条件を実際に作り、**Alertmanager の `/api/v2/alerts` に届く**こと。
      届かないものは理由を書いて別 issue へ分ける
- [x] 5. `check-grafana-alerting.js` が Prometheus / Grafana の 1 対 1 を保ったまま通る
- [x] 6. 運用仕様書 §監視・アラート の SLO 表を追随させる
- [x] 7. 退行防止の判断（何回目か）を IADR と PR 本文に書く
- [x] 8. **クラスタを作業前の状態（fail-safe 構成）へ戻す**

## テスト方針（変異試験の設計）

**式が真になる条件を、実際に作れるか／作れないかで手段を分ける。**

| ルール | 変異の作り方 | 予想される限界 |
| --- | --- | --- |
| `OtelCollectorDown` | `kubectl scale deploy/otel-collector --replicas=0`（#1112 の手順を踏襲） | 無し（実測済み） |
| `ServiceRequestMetricsAbsent` | しきい値を割る条件＝「直近 15 分に受信があり、直近 5 分は 0」。**アプリを止めるのではなく**、`offset 15m` の履歴が要る | **転送を有効にしてからの経過時間**に律速される |
| `HighHttp5xxRate` | 5xx を返す経路を叩く（存在しないパスは 404 なので不可。**500 を返す入口**が要る） | 5xx を意図的に出せる入口の有無 |
| `SearchLatencyP95High` | 実レイテンシを 1.5s 超にはできない → **同じ式で閾値だけ下げた合成ルール**で「評価対象があり真になる」ことを示す | **本物のルールの発火ではない**。区別して書く |
| `RagLatencyP95High` | `/analysis/ask` を呼んで `http_route` を生やす → 同上 | 同上 |

🔴 **合成ルールでの確認は「発火した」と書かない。**「**式が評価対象を持ち、閾値を割れば真になる**」
までである（#1112 が `OtelCollectorDown` について「合成ルールではなく実ルールである」と
わざわざ書いた区別を守る）。届かなかったものは受け入れ基準 4 に従って別 issue へ分ける。

## 計画書との差異

- **差異: あり（計画の記述が事実と食い違う）。**
  NFR-21 の 2026-08-08 注記と ADR-0006 の同日追記、および
  `06_technical/05_observability-ops.md` §アラートの通知経路 の表は、いずれも
  **「アラートルール（SLO ベース・5 件）は存在し、Prometheus が評価している」**
  **「欠けていたのは検知そのものではなく通知の送り先である」**と書いている。
  **実測はこれを否定する** —— 5 件中 4 件は評価対象を持たず、通知を配線しても永久に発火しない。
  本 PR は実装側を直すが、**計画側の「検知は足りている」という前提は残る。**
  → **planning へ issue を起票して環流する**（`/plan-feedback`）。実装側で計画書を書き換えない。

## 未決事項（実測で閉じた分を含む）

- ✅ **閉じた: Grafana 側の provisioning は受理される。** `/api/v1/provisioning/alert-rules` が
  5 件を `provenance: "file"` で返す（#665 §判断 0 の積み残しを閉じた）。
  🔴 **同時に、稼働 Grafana には旧式が残っていた**ことも判明した（Prometheus 側だけ直っていた）。
  是正版を apply → `POST /api/admin/provisioning/alerting/reload` で 4 件とも是正を確認した。
- ✅ **閉じた（否定形で）: `HighHttp5xxRate` の変異試験に使える 5xx の入口は無かった。**
  `/analysis/ask` へ不正な本文・負の `topK`・巨大な `topK` を送っても 200 か 400 で、5xx は作れない。
  TSDB には `503`（5 サービスの `/health/ready`・起動直後の失敗）が**実在する**が、
  これは過去の累積であり `rate(...[5m])` は現在 0 である。
  → **ステータス選択を `5..` → `2..` へ変異させた合成プローブで代替した**（§実測 C）。
  この変異はラベル名・除算・`by (job)` の grouping を**そのまま**残すので、
  **5xx が実際に出た瞬間に本番ルールが発火することは示せている。**
- 🔴 **残る未決: 本番 `ServiceRequestMetricsAbsent` の `firing` までの到達は実測していない。**
  `pending` には変異なしで入った（実在の `job` を伴う）が、`for: 5m` を満たすには
  サービスを 5 分止める必要があり、**他エージェントと共有するクラスタでの許可範囲外**とした。
- 🔴 **残る未決: `RagLatencyP95High` は `/analysis/ask` が呼ばれない限り評価対象を持たない。**
  仕様どおりだが、**「鳴らない」と「鳴りようがない」の区別が付かない**。
  無風時に区別するには合成監視が要る（本 PR の射程外）。
