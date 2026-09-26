---
title: 作業仕様書 — Grafana 版の OtelCollectorDown が「== 0 の絞り込み」と「gt 0 の評価器」の組み合わせで発火し得ない（#1577）
type: spec
status: in-progress
related_ids: [NFR-21, ADR-0006, ADR-0076, IADR-0165, IADR-0345, IADR-0370, IADR-0432]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21 障害検出 5 分以内)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (決定 42 暫定の一次検知)
related_specs: [20260926_1544_reset-floor-zero-endpoint-alert.md, 20260904_issue-1202_absent-series-slo-alerts.md]
issue: "#1577"
---

# 作業仕様書 — Grafana 版の OtelCollectorDown が発火し得ない組み合わせを直し、同型を検査器で止める

## 起点

- issue: #1577（#1544 の作業〔PR #1576〕で見つかった既存の不具合）。
- 要求: NFR-21（障害検出 5 分以内）。暫定の一次検知（Grafana 統合アラート）は計画の可観測性の技術検討 決定 42、
  アラートの正は ADR-0006（Alertmanager。改めない）。
- 実装判断の記録先: **新しい IADR は起こさない。** 検査器を持つ IADR-0165 と、`noDataState` の理由を持つ IADR-0370 へ
  `［2026-09-26 追記 / #1577］` を足す（IADR の欠番を作らない方針・日付つき追記を優先する方針）。

## 現状（着手時に確かめた。2026-09-26・`origin/develop` = e30e1c76）

| # | 事実 | 確かめた場所 |
| --- | --- | --- |
| 1 | Grafana 版 `OtelCollectorDown` は `expr: 'up{job="otel-collector"} == 0'`・`evaluator: { type: gt, params: [0] }`・`noDataState: NoData` | `deploy/grafana/provisioning/alerting/slo-alerts.yaml` 48〜73 行（k8s inline も同内容） |
| 2 | 絞り込みの後に残る値は 0 なので `0 > 0` は常に偽。**正常時（`up = 1`）は式が空 → `NoData`** になり、**鳴る向きが逆** | IADR-0370 §D の実測（通常時 `OtelCollectorDown alerts=1 [NoData]`・`ServiceRequestMetricsAbsent alerts=1 [NoData]`） |
| 3 | Prometheus 版の同名規則は正しい（Prometheus は系列の有無で鳴るので値 0 で構わない） | `deploy/prometheus/alerts.yml` 42 行・`deploy/local/observability/prometheus.yaml` 46 行 |
| 4 | 模範の形: #1544 の `ResetFloorNoReadyEndpoint` は `up{job="reset-floor"}` を生で取り `lt 1`・`noDataState: OK` | `slo-alerts.yaml` の `reset-floor-availability` 群・`scripts/reset-floor.test.js` 477 行 |
| 5 | Grafana の版は 11.0.0。閾値式の評価器は `gt` / `lt` / `within_range` / `outside_range`（等号の評価器は無い前提で設計する） | `deploy/docker-compose.yml` 273 行・`deploy/local/observability/grafana.yaml` の image |
| 6 | 検査器 `check-grafana-alerting.js` は 5 点（件数・名前・datasourceUid・compose/k8s 同内容・必須キー）だけを見ており、式と評価器の関係は見ていない | `scripts/check-grafana-alerting.js` 冒頭 |

## 全規則の走査（依頼の「同型の組み合わせが他に無いか」）

**母集合**: Grafana の写し 2 つ（compose `deploy/grafana/provisioning/alerting/slo-alerts.yaml` と k8s の inline
`deploy/local/observability/grafana.yaml` の `slo-alerts.yaml: |`）の**全 20 規則ずつ**。
**引き方**: 手で全 20 件の expr・評価器を読んだうえで、本作業で足した検査 6 の読み取り（`grafanaRuleConditions` / `expressionValueSet`）を
**是正前の**両ファイルへ当てて機械でも確かめた（スクラッチのスクリプト。コミットしない）。両写しの結果は一致した。

| 規則 | 式の最上位の絞り込み | 絞り込みの後に残り得る値 | 評価器 | 判定 |
| --- | --- | --- | --- | --- |
| OtelCollectorDown | `up{…} == 0` | {0} | gt 0 | 🔴 **発火し得ない** |
| ServiceRequestMetricsAbsent | `rate(…) == 0 and on (job) (… offset 15m > 0)`（`and` は左辺の値を残す） | {0} | gt 0 | 🔴 **発火し得ない**（issue に無い発見） |
| HighHttp5xxRate | なし（比） | 縛られない | gt 0.05 | ok |
| SearchLatencyP95High | なし（`histogram_quantile`） | 縛られない | gt 1.5 | ok |
| RagFirstTokenP95High | なし | 縛られない | gt 5 | ok |
| RagLatencyP95High | なし | 縛られない | gt 5 | ok |
| OtelCollectorUpSeriesAbsent ほか `absent()` 5 件（HttpServerMetrics / SearchLatency / RagLatency / MailRelayQueue / ResetFloorUp） | なし（`absent` は真で 1） | 縛られない | gt 0 | ok |
| KnowledgeHealth…ProducerAbsent 2 件（`absent_over_time`） | なし | 縛られない | gt 0 | ok |
| UnitDocumentsMissingProjectAttribute | `increase(…) > 0` | (0, ∞) | gt 0 | ok |
| DepartmentSyncNotCorrecting | `(… or vector(0)) + (… or vector(0)) > 0`（`or` は括弧の中） | (0, ∞) | gt 0 | ok |
| MailRelayDeferredBacklog | なし（ゲージ） | 縛られない | gt 0 | ok |
| MailRelayDeferredMessageNearExpiry | なし（ゲージ） | 縛られない | gt 1200 | ok |
| ResetFloorNoReadyEndpoint | なし（生の `up`） | 縛られない | lt 1 | ok |
| LlmMonthlyBudgetExceeded | `… > on (…) max by (…) (…)`（ベクタどうし。左辺の値が残る） | 縛られない | gt 0 | ok |

計 20 件（上表は 14 行で、`absent()` の 6 件を 1 行、`absent_over_time` の 2 件を 1 行にまとめている。14 − 2 ＋ 6 ＋ 2 ＝ 20。
機械の走査も両写しで `20 rules` を返した）。
**同型は 2 件（OtelCollectorDown / ServiceRequestMetricsAbsent）で、両写しに同じく存在した。**

機械の走査の生出力（是正前・compose 分。k8s inline 分も同一）:

```
== compose: 20 rules
  OtelCollectorDown | values={0} | gt [0] | NEVER-FIRES
  ServiceRequestMetricsAbsent | values={0} | gt [0] | NEVER-FIRES
  HighHttp5xxRate | values=(-Infinity,Infinity) | gt [0.05] | ok
  SearchLatencyP95High | values=(-Infinity,Infinity) | gt [1.5] | ok
  RagFirstTokenP95High | values=(-Infinity,Infinity) | gt [5] | ok
  RagLatencyP95High | values=(-Infinity,Infinity) | gt [5] | ok
  OtelCollectorUpSeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  HttpServerMetricsSeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  SearchLatencySeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  RagLatencySeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  MailRelayQueueSeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  ResetFloorUpSeriesAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  KnowledgeHealthUnresolvedLinksProducerAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  KnowledgeHealthEdgeTypeUsageProducerAbsent | values=(-Infinity,Infinity) | gt [0] | ok
  UnitDocumentsMissingProjectAttribute | values=(0,Infinity) | gt [0] | ok
  DepartmentSyncNotCorrecting | values=(0,Infinity) | gt [0] | ok
  MailRelayDeferredBacklog | values=(-Infinity,Infinity) | gt [0] | ok
  MailRelayDeferredMessageNearExpiry | values=(-Infinity,Infinity) | gt [1200] | ok
  ResetFloorNoReadyEndpoint | values=(-Infinity,Infinity) | lt [1] | ok
  LlmMonthlyBudgetExceeded | values=(-Infinity,Infinity) | gt [0] | ok
```

**Prometheus の写し（compose `deploy/prometheus/alerts.yml`・経路 B の inline）は走査の対象外**: Prometheus は式が系列を返すかどうかで鳴り、
値の大きさを比べる評価器を持たないので、この組み合わせの欠陥が原理的に起きない。**変えない**（`check-prometheus-alerts-parity.js` の 1 対 1 もそのまま）。

## 設計判断

### OtelCollectorDown: `up` を生で取り `lt 1`、`noDataState: OK`（依頼どおり #1576 の形）

- 式 `up{job="otel-collector"}`・評価器 `{ type: lt, params: [1] }`（値 0 で真）。`for: 2m`・severity は変えない。
- `noDataState: OK`: 式が空になるのは `up` の系列そのものが無いときだけで、それは `OtelCollectorUpSeriesAbsent`（`absent(up{job="otel-collector"})`・
  `noDataState: OK`）が拾う。`NoData` にすると同じ不在で 2 通鳴る（#1544 の `ResetFloorNoReadyEndpoint` と同じ理由）。
  Prometheus 版も系列が無ければ空ベクタで鳴らないので、**意味は Prometheus 版と一致する**。

### ServiceRequestMetricsAbsent: `== 0` を `== bool 0` に直し `gt 0` のまま、`noDataState: OK`

- 式 `sum by (job) (rate(…[5m])) == bool 0 and on (job) (sum by (job) (… offset 15m) > 0)`。`bool` で途絶の job は 1、受信中の job は 0 になり、
  `and on (job)` の右辺（15 分前に受信していた job だけ）は Prometheus 版のまま。評価器 `gt 0` は据え置き。
- **採らない案**: 生の `rate` を `lt ε` で比べる —— 途絶は `rate == 0` の厳密な等号で、ε が新しい閾値を持ち込む。Grafana 11.0.0 には等号の評価器が無い（現状 5）。
- `noDataState: OK`: 空になるのは「15 分前に受信していた job が 1 つも無い」（途絶ではない。起動直後・閑散時）か、系列ごと無いとき
  （`HttpServerMetricsSeriesAbsent` が拾う）。Prometheus 版も空なら鳴らない。

### 検査器: `check-grafana-alerting.js` に検査 6 を足す（依頼の「検出する検査」）

- 各ルールの expr の**最上位の比較**（`bool` なし・片辺が数値リテラル）から発火側で残り得る値の集合を**区間**で求め、評価器を満たす値の集合と
  **交わらなければ**違反（「永久に発火しない」）。`== 0` × `gt 0` に限らず `< 1` × `gt 1` などの同型も拾う。
- PromQL の優先順位どおりに読む: `or` は各辺の和、`and` / `unless` は左辺、`bool` は {0, 1}、ベクタどうしの比較（`on (…)` 修飾・両辺が非リテラル）は値を縛らない、
  式全体を包む括弧は剥がす、文字列リテラル（ラベル値）の中の `==` / `or` は読まない。
- **偽陰性は受容し、偽陽性は出さない**: 比較の結果へさらに算術・集約を掛けた形（`(up == 0) * 1` 等）は値を縛らないものとして読む。
- **fail-closed**: expr が 1 件でない・評価器を 1 件読めない・評価器の型を解釈できないルールは違反。判定できたルールが 0 件なら違反（0 件走査の門）。
- **写しの両方**（compose / k8s inline）を見て、違反文に `[compose]` / `[k8s inline]` を付ける（検査 4 が同内容を見るが、乖離時に片方の違反を黙らせない）。
- 「同型の事故が 2 回起きたら」: 同型が 2 件実在し、#1544 でも同じ形が書かれかけた。IADR-0345 決定 5 / IADR-0370 決定 7（稼働 TSDB の実在を見る静的検査は作らない）とは軸が違う
  （本検査はリポジトリ内の expr と評価器の自己整合）。IADR-0165 の追記に書く。

## 母集合（誤りになる記述の走査。着手時に引き直した）

`git grep`（パス除外: `src/ai-stock-trading` / `CHANGELOG.md` / `.ai-context/specs/`。拡張子で絞らない）。

| 軸 | 検索 | 拾ったもの |
| --- | --- | --- |
| 1 対象の規則名 | `OtelCollectorDown` | 両 Grafana 写し・Prometheus 2 写し・`deploy/local/infra/otel-collector.yaml` 104・`docs/operations/operations.md` 786・IADR 0304/0322/0345/0354/0370/0421/0432 |
| 2 `noDataState` の言明 | `noDataState` / `既存 6 件` / `他の 6 件` | 両 Grafana 写しの evaluation-target 群の前書き・`docs/operations/operations.md` 850・IADR-0370 88〜96/207〜213・IADR-0420 157・IADR-0466 115・IADR-0473 86 |
| 3 検査器の範囲の言明 | `check-grafana-alerting` | 両 Grafana 写しの冒頭（「機械で確かめたのは…まで」）・`docs/operations/operations.md` 708/772/793・IADR-0165 108/124/158・IADR 0168/0304/0345/0354/0389/0399/0421/0432/0466・`scripts/check-*-parity.js`・`deploy/local/observability/prometheus.yaml` 29・`docs/observability/rag-first-token-latency.md` 112 |
| 4 絞り込み × 評価器の言明 | `== 0.*gt 0` / `gt 0.*== 0` | IADR-0432 392・IADR-0473 87・`scripts/reset-floor.test.js` 477/520・両 Grafana 写しの床と部門の同期の前書き |
| 5 件数（導出値） | `19 ルール` / `＝ \*\*19` | `docs/operations/operations.md` 706（「同じ 19 ルール」）・両 Grafana 写しの冒頭の内訳（「＝ **19**」。上の行は 20） |

**直すもの**: 両 Grafana 写しの 2 規則と前書き（冒頭の注意・群の注記・evaluation-target 群の「既存 6 件」への追記・検査器の範囲・内訳の件数）、
`docs/operations/operations.md` 772（検査器の範囲）・850 直後（2 行の `noDataState: OK` の追記）・706（件数 19 → 20）、
`scripts/check-grafana-alerting.js`（検査 6・冒頭の 6 点）、`scripts/scripts.repo.test.js`（名指しの self-test に 3 件・実データの変異・0 件走査の門）、
IADR-0165 と IADR-0370 へ日付つき追記。

**除外したものと理由**

- Prometheus の 2 写し・`deploy/local/infra/otel-collector.yaml` 104・IADR-0304/0322/0345/0354/0421: Prometheus 版の `OtelCollectorDown` の記述・実測であり、正しい（現状 3）。
- `docs/operations/operations.md` 786: Alertmanager 経由の発火の実測（Prometheus 版）。正しい。
- IADR-0370 88〜96（決定 4 の本文）・207〜213（実測）: 凍結記録。本文は書き換えず、日付つき追記で「2 件について誤り」を記録する。
- IADR-0432 392・IADR-0473 87・`scripts/reset-floor.test.js` 477/520・床と部門の同期の前書き: 「`== 0` を `gt 0` で比べると永久に発火しない」「本規則はその形ではない」の言明であり、正しいまま。
- IADR-0165 108/158・IADR-0168/0304/0345/0354/0389/0399/0421/0432/0466・`scripts/check-*-parity.js`・`prometheus.yaml` 29・`rag-first-token-latency.md` 112:
  検査器の存在・1 対 1 の突合・当時の件数を述べるだけで、検査の点数や式と評価器の関係を主張していない（IADR-0165 124〜131 の「5 点」は本文を残し追記で 6 点目を記録する）。
- IADR-0420 157・IADR-0466 115: それぞれの規則の `noDataState: OK` の理由であり、本件で変わらない。
- `docs/operations/operations.md` 793（削除時の手順）: 本件で変わらない。
- `deploy/grafana/provisioning/alerting/slo-alerts.yaml` の「式は alerts.yml と同じものを使う」: 冒頭の新しい注意（絞り込みの式を写さない）が例外を明示するので残す。
- `.github/workflows/`: 検査器と `scripts.repo.test.js` は既存の `ci.yml`（`scripts-tests` ジョブ等）が走らせており、起動条件・必須チェックは変えない。

## 受け入れ基準

1. Grafana 版 `OtelCollectorDown` が `expr: 'up{job="otel-collector"}'`・`evaluator: { type: lt, params: [1] }`・`noDataState: OK` を持つ（compose / k8s inline の両方）。
2. Grafana 版 `ServiceRequestMetricsAbsent` が `== bool 0` の式・`gt 0`・`noDataState: OK` を持つ（両方）。
3. Prometheus の 2 写しは変わらず、`check-prometheus-alerts-parity.js` / `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` / `reset-floor.test.js` が通る。
4. `check-grafana-alerting.js` の検査 6 が、`== 0` × `gt 0`・`and` の左辺の `== 0`・`< 1` × `gt 1` を違反にし、`bool`・`> 0` × `gt 0`・`or` の片側・括弧の中の `or`・
   ベクタどうしの比較・ラベル値の中の `==` を違反にしない。評価器を読めない・型を解釈できないルールを違反にする（自己試験）。
5. `scripts/scripts.repo.test.js` が、実データを #1577 以前の形へ戻す変異（2 規則それぞれ）で**両写しの**違反を検出すること、実データで判定件数が 1 件以上であること、
   名指しの self-test（検査 6 の 3 件）が走っていることを固定する。
6. 走査の結果（上表）と除外理由が本仕様書にある。

## 未検証（稼働クラスタへ当たらない作業である）

- **是正後の Grafana 版の発火は稼働 Grafana で確かめていない。** 配備時の確かめ方: `GET /api/prometheus/grafana/api/v1/rules` で通常時に
  `OtelCollectorDown` / `ServiceRequestMetricsAbsent` が `Normal`（`NoData` ではない）であること、collector を 0 台へ落として `OtelCollectorDown` が
  `for: 2m` の後に `Alerting` になること（#1112 の手順）。Grafana が provisioning を受理するかは IADR-0165 決定 1 のまま。

## 検証（証跡は PR 本文）

- `node scripts/check-grafana-alerting.js`（実データで違反 0 件・判定 20 件）、`--self-test`（23 件）
- `node scripts/check-prometheus-alerts-parity.js` / `node scripts/check-grafana-provisioning-parity.js` / `node scripts/reset-floor.test.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `node scripts/check-trace-blocks.js` ほか文書の検査
