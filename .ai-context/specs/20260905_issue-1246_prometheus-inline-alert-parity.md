---
title: 経路 B の Prometheus inline が compose のアラートルールから 2 件遅れていたのを埋め、乖離を止める門を置く
type: spec
status: done
related_ids:
  - NFR
  - NFR-21
  - FR-10
  - SC-10
  - ADR-0006
  - ADR-0076
  - IADR-0130
  - IADR-0168
  - IADR-0370
  - IADR-0389
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-stack.md
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md (決定 3)
---

# 作業仕様書: 経路 B の Prometheus inline の追随と、乖離を止める門（#1246 の取りこぼし）

## 背景

`deploy/local/observability/prometheus.yaml`（経路 B・k8s）は、compose 側の
`deploy/prometheus/alerts.yml` のアラートルールを **inline で二重管理**している
（kustomize の root 外参照制約に従う。同ファイル冒頭が方針として宣言している）。

**その inline が遅れていた。** #1246 が compose 側へ足した `knowledge-health-producers` の 2 件が
inline に無い。PR #1286（#1203）はこの乖離を**発見して記録したが、射程外として埋めなかった**
（「knowledge-health-producers の 2 件は #1246 の射程なのでここでは足していない（受容として記録する）」）。

**本作業はその受容を解除する。**

## 母集合（自分で引いた。基点・生出力・陽性対照つき）

基点 `origin/develop` `4eff9bb4`。

```console
$ git rev-parse --is-shallow-repository
false
```

### 走査 1 —— 両ファイルのルール名（生出力）

```console
$ grep -oE "^\s+- alert: [A-Za-z]+" deploy/prometheus/alerts.yml        → 12 件
$ grep -oE "^\s+- alert: [A-Za-z]+" deploy/local/observability/prometheus.yaml → 10 件
```

**差は 2 件**: `KnowledgeHealthUnresolvedLinksProducerAbsent` / `KnowledgeHealthEdgeTypeUsageProducerAbsent`
（どちらも群 `knowledge-health-producers`）。

### 走査 2 —— 差の**軸**（何が一致していて何が違うか。門の設計に直結する）

10 件の共通ルールについて、群名・`expr`・`for`・`severity` を機械で突合した（コメント行を落として比較）。

```
compose rules: 12 / inline rules: 10
 ok  OtelCollectorDown / ServiceRequestMetricsAbsent / HighHttp5xxRate / SearchLatencyP95High /
     RagFirstTokenP95High / RagLatencyP95High / OtelCollectorUpSeriesAbsent /
     HttpServerMetricsSeriesAbsent / SearchLatencySeriesAbsent / RagLatencySeriesAbsent
MISSING in inline: [knowledge-health-producers] KnowledgeHealthUnresolvedLinksProducerAbsent
MISSING in inline: [knowledge-health-producers] KnowledgeHealthEdgeTypeUsageProducerAbsent
```

🔴 **10 件すべてで群名・`expr`・`for`・`severity` が一致する。違うのは `summary` / `description` だけである**
（inline は意図的に凝縮した文面を持つ。例: compose が生のメトリクス名を書く箇所を inline は
「レイテンシ分布が無い」と言い換え、末尾の ADR 引用を落としている）。

**したがってバイト一致は課せない。課せるのは「群名 ＋ ルール名 ＋ `expr` ＋ `for` ＋ `severity` の 1 対 1」である。**

### 走査 3 —— 同型の二重管理が他に幾つあるか（規則 5: 軸を 1 本で終わらせない）

```console
$ grep -rn "inline|同内容|二重管理" deploy/local/ --include=*.yaml
deploy/local/observability/alertmanager.yaml:4-5   ← deploy/alertmanager/alertmanager.yml
deploy/local/observability/grafana.yaml:2          ← datasources.yaml
deploy/local/observability/grafana.yaml:403-404    ← slo-alerts.yaml（**検査器あり**）
deploy/local/observability/loki.yaml:1             ← loki-config.yaml
deploy/local/observability/prometheus.yaml:2-3     ← deploy/prometheus.yml
deploy/local/observability/prometheus.yaml:24      ← deploy/prometheus/alerts.yml（**本件**）
```

**宣言された二重管理は 6 組。うち機械で守られているのは 1 組だけである。**

**陽性対照**（「1 組しか無い」を「無い」と読む前に走査器が生きていることを確かめた）:
同じ走査は `grafana.yaml:404` の「乖離は `scripts/check-grafana-alerting.js` が止める」という
**検査器の存在を明示した行**をヒットさせる。

### 除外理由（本 PR で扱わないもの）

| 対象 | 理由 |
| --- | --- |
| `alertmanager.yaml` / `loki.yaml` / `datasources` / `prometheus.yml` 本体の inline | **同型だが本件ではない。** 実際に遅れたのはアラートルールだけであり、他が遅れた実測はまだ無い。**「同型だから今のうちに全部」は母集合の取り違えの逆側**（起きていない事故のために門を増やす）。本 PR は**実際に遅れた 1 組**に門を置き、残り 4 組は実装 ADR に「同じ形の露出がある」と記録するに留める |
| `check-grafana-alerting.js` の突合軸（Prometheus ↔ Grafana の 1 対 1） | 別の軸。重ならない（同スクリプト冒頭 §と `check-grafana-provisioning-parity.js` 冒頭 ★ が明記） |
| `check-grafana-provisioning-parity.js` の射程（経路 A/B の Grafana provisioning） | Grafana 配下が対象。Prometheus 側は入っていない |

## 🔴 決定: 門を置く —— 同型の事故は **既に 2 回**起きている

規約は「**同型の事故が 2 回起きたら**検査器を足す（1 回目は記録に留める）」と定める。
**本件はその条件を満たしている。** 数えたのは「**経路 B の inline が compose の原本から静かに遅れた**」という型である。

| 回 | 事故 | 出所（実装側の記録） |
| --- | --- | --- |
| **1 回目** | 経路 B の Grafana に **`dashboards/` が丸ごと無い**ことを誰も見ていなかった。しかもその中の `llm-usage.json` は月次 LLM 費用確認 runbook の**行き先**だった | `scripts/check-grafana-provisioning-parity.js` 冒頭 ★（#674 / `IADR-0168`）——「**検査していたのに見つからなかったのではなく、検査の射程が狭かった**」 |
| **2 回目** | 経路 B の Prometheus inline が #1246 の 2 件に追随せず、**2 世代残った** | `deploy/local/observability/prometheus.yaml:26-31`（#1203 / PR #1286 が発見して受容として記録）。**同じ取りこぼしは `check-grafana-alerting.js:14-16` の件数注記にも起きていた**（「9 件」のまま 2 世代） |

**1 回目が門を生んだのに、その門の射程外で 2 回目が起きた。** これは「射程を広げる」形の再発であり、
`check-grafana-provisioning-parity.js` が #674 で解いた問題と**同じ形**である。

## 設計

### 変更 1: inline へ 2 件を足す

`knowledge-health-producers` 群を inline へ追加する。**凝縮の作法は既存の inline に揃える** ——
`expr` / `for` / `labels` は compose と一字一句同じ、`summary` は同じ、
`description` は「生産者が居ない」という因果の鎖を残しつつ短くする（既存 10 件と同じ扱い）。

群の直前に、`absent_over_time([2h])` を使う理由を **1 行**で置く
（compose 側は 12 行の詳説を持つが、inline は凝縮側なので要点だけ。**理由を消さない**）。

### 変更 2: 冒頭の日付つき注記を更新する

現在の注記は「本 PR は 10 / 12 件にしたが、2 件は #1246 の射程なのでここでは足していない（受容として記録する）」。
**受容を解除したので、そう書き直す。** 🔴 **#1203 の記録を消さない** —— いつ・なぜ受容し、いつ・なぜ解いたかを
両方残す（`.ai-context/` ではないので日付つき追記の書式は課されないが、経緯を消すと同じ判断を繰り返す）。

### 変更 3: 門 `scripts/check-prometheus-alerts-parity.js` を新設する

- **突合軸**: 群名 ＋ ルール名 ＋ `expr` ＋ `for` ＋ `severity` の 1 対 1。
  🔴 **`summary` / `description` は突合しない**（inline は意図的に凝縮しており、比較すると常に赤になる）
- **コメントアウトされた follow-up 群は両側とも除く**（compose 側の `platform-messaging` 等）
- **fail-closed**（`IADR-0130`）: 走査結果が 0 件なら fail する
- `--self-test` を持ち、`scripts.repo.test.js` が実データ ＋ 変異試験で固定する
  （`check-grafana-alerting.js` と同じ配線。CI では必須 check `scripts-tests` が回す）

**なぜ既存の検査器を拡張しないか**:

| 案 | 評価 |
| --- | --- |
| `check-grafana-alerting.js` を拡張 | ❌ 軸が違う。あちらは「Prometheus のルール ↔ Grafana のルール」の 1 対 1 であり、経路 A/B のパリティではない（同スクリプトが自ら「本検査器で確かめられないこと」を先に書いている作法に反する） |
| `check-grafana-provisioning-parity.js` を拡張 | ❌ 名前が `grafana-*` であり、Prometheus を入れると**名前が嘘になる**。同スクリプトは basename 突合で ConfigMap の data キーを見る作りで、`alerts.yml` の**凝縮を許す**比較を持たない |
| **新設** | ✅ 軸が独立し、名前が中身と一致する。既存 2 本の冒頭が「重ならない別の軸である」と宣言している作法に揃う |

## 受け入れ基準

- [x] inline のルールが **12 件**になり、compose と群名・ルール名・`expr`・`for`・`severity` が 1 対 1 で一致する
- [x] `summary` / `description` は凝縮のまま（**バイト一致を課していない**）
- [x] 冒頭の注記が受容の解除を記し、**#1203 が受容した経緯も残っている**
- [x] `node scripts/check-prometheus-alerts-parity.js` が緑で、**2 件を消すと赤になる**（変異試験）
- [x] 走査 0 件で fail する（`--self-test` と `scripts.repo.test.js` で固定）
- [x] `check-grafana-alerting.js` / `check-grafana-provisioning-parity.js` が緑のまま（既存の軸を壊していない）

## テスト方針

`scripts/scripts.repo.test.js` へ `check-grafana-alerting` と同じ 5 種を置く ——
①`--self-test` が通る ②実データで違反 0 件 ③0 件走査の下限（1 件以上拾う）
④**実データのルールを 1 件消すと違反を出す**（変異試験）⑤対象ファイルが無いと fail する。

## 計画書との差異

- 差異: なし

## 未決事項

- 残る 4 組の二重管理（alertmanager / loki / datasources / `prometheus.yml` 本体）には門を置かない。
  **実際に遅れた実測がまだ無い**ためである。実装 ADR に「同じ形の露出がある」と記録し、
  遅れが 1 回でも観測されたらそのとき判断する。
