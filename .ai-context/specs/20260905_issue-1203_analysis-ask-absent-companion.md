---
title: 作業仕様書 — 合成監視が着地したので /analysis/ask 系を absent の対象へ入れ、失効した繰り延べの注記を引き取る
type: spec
status: done
related_ids:
  - NFR-02
  - NFR-21
  - ADR-0006
  - ADR-0076
  - ADR-0079
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "ADR-0076 決定 3（常時トラフィックがある経路の SLO は、系列の不在そのものを warning とする。absent() / absent_over_time() を併設する。判定基準は『その経路が無風でいられる時間が検知要件（5 分）より短いこと』。無風が 5 分を超え得る経路（/analysis/ask 系）は対象外とし、決定 4 の合成監視で常時トラフィックを作ってから対象へ入れる）"
  - "ADR-0076 決定 4（合成監視を計画へ位置づける。標識と除外は同時に入れる。除外できない構成では配備しない）"
  - "ADR-0076 理由（決定 3 が限定適用なのは、恒常発火が警報を無効化するためである）"
  - "ADR-0079 決定 1（合成監視の実行間隔を 2 段に分ける。常時トラフィックの生成＝60 秒・LLM を呼ばない／SLO 評価用＝60 分・LLM を呼ぶ）"
  - "ADR-0079 決定 2（LLM を呼ぶ合成トラフィックを認める。費用の上限は絶対額で置かず間隔で実質的に固定する。60 分＝月 720 回・概算 月約 4,400 円）"
  - "ADR-0079 決定 3（NFR-02 の SLO 判定窓を合成の標本量に合わせて広げる。具体値は実装が実測で定める。検知遅延の許容上限は 8 時間）"
  - "ADR-0079 決定 4（意図的に 5xx を出す合成経路は置かない。ADR-0076 決定 4 の部分改定）"
  - "ADR-0079 結果（absent() 系の警報が低頻度経路にも及ぶ。決定 3 の対象外だった /analysis/ask 系が対象へ入る）"
  - "ADR-0079 フォローアップ 2・3（SLO 評価用の合成を 60 分間隔で置き AllowLlmEgress を有効にする / RagFirstTokenP95High の評価窓を広げ実測値を環流する）"
  - "02_requirements/01_requirements.md NFR-02（RAG 回答 初回応答 p95 5 秒）/ NFR-21（障害検出 5 分以内）"
related_adrs:
  - IADR-0370
  - IADR-0378
  - IADR-0354
  - IADR-0389
  - IADR-0345
  - IADR-0141
issue: "#1203"
---

# 作業仕様書: `/analysis/ask` 系を `absent` の対象へ入れる（#1203 の残り 1 点）

## 起点

#1203 のうち標識・除外・配備物（opt-in オーバーレイ）は PR #1259 で着地し、IADR-0378 に記録済みである。
**残っているのは 1 点だけ** —— `deploy/prometheus/alerts.yml` が `/analysis/ask` 系を
`ADR-0076` 決定 3 の `absent` 併設から外している注記が、**その理由（「決定 4 の合成監視が入るまで」）を
失ったこと**である。

さらに **本作業の着手時点で、繰り延べの理由は 2 段階で失効している。**

| # | 失効させた事実 | 出所 |
| --- | --- | --- |
| 1 | 合成監視の常駐プローブが着地した（60 秒・`/bff/analysis/ask` と `/bff/analysis/ask/stream`） | 本リポジトリ `deploy/local/synthetic-monitor/`（PR #1259） |
| 2 | **計画が実行間隔と費用の扱いを確定させた**（`ADR-0079`。利用者裁定 2026-09-05・環流 planning#538） | planning `07_adr/ADR-0079_synthetic-monitoring-interval-and-slo-window.md` |

🔴 **② は着手時に初めて分かった。** IADR-0378 §影響・残るもの は「`absent` の対象拡大は
**頻度の裁定が下りてから**である」と書いており、**その条件は本作業の着手前に満たされていた。**
`ADR-0079` §結果 は「**`absent()` 系の警報が低頻度経路にも及ぶ**（決定 3 の対象外だった
`/analysis/ask` 系が対象へ入る）」と明示している。**本作業はその 1 文の実装である。**

## 対象範囲

- **対象**: `/analysis/ask` 一括経路の HTTP 系列に対する `…SeriesAbsent` の 1 件追加（Prometheus 版・
  Grafana 版・k8s inline の 3 系統）と、**失効した繰り延べの注記の引き取り**。
- **対象外（本 PR では行わない。理由は §未決事項）**:
  - `ADR-0079` フォローアップ 2（SLO 評価用の 60 分間隔プローブの配備・`AllowLlmEgress` の有効化）
  - `ADR-0079` フォローアップ 3（`RagFirstTokenP95High` の評価窓を広げ、実測値を環流する）
  - `rag_answer_first_token_duration_seconds_bucket` に対する `…SeriesAbsent`（フォローアップ 2 の従属）
  - 静的検査器の新設（`IADR-0345` 決定 5 の繰り延べを覆さない。`ADR-0076` 決定 3 🔴 が明示）

## 母集合（自分で引いた。基点・走査コマンド・陽性対照つき）

**基点コミット `3663b2baf46c2444efb73eb725b8366ac160a2ac`（`origin/develop`）。**

```console
$ git rev-parse --is-shallow-repository
false
```

🔴 **`false` を先に確かめている。** `true` なら `git log` / `git grep` の結果は履歴の打ち切り位置を
指し得るため出典に使えない（planning#410）。

### 走査 1 —— 繰り延べの語そのもの（母集合 A）

```console
$ git grep -nE "決定 ?4 の合成監視" -- . ':!src/ai-stock-trading'
.ai-context/adr/IADR-0354_rag-first-token-latency-metric.md:244
.ai-context/adr/IADR-0370_slo-evaluation-target-absent-rules.md:64
.ai-context/adr/IADR-0378_synthetic-traffic-marker-and-exclusion.md:46
.ai-context/specs/20260903_issue-1204_rag-first-token-ttft-metric.md:234
.ai-context/specs/20260904_issue-1202_absent-series-slo-alerts.md:15
.ai-context/specs/20260905_issue-1203_synthetic-monitoring-marker-and-exclusion.md:17
deploy/local/observability/prometheus.yaml:87
deploy/prometheus/alerts.yml:93
deploy/prometheus/alerts.yml:128

$ git grep -nE "合成監視待ち|合成監視が着地|作ってから対象へ入れる" -- . ':!src/ai-stock-trading'
  → 上の 9 件のうち 6 件 ＋ docs/operations/operations.md:714（**新規に 1 件増えた**）

$ git grep -nE "対象外" -- deploy docs scripts | grep -Ei "absent|評価対象|/analysis/ask|合成"
  deploy/local/observability/prometheus.yaml:87
  deploy/prometheus/alerts.yml:93
  deploy/prometheus/alerts.yml:127
  docs/operations/operations.md:713
  （docs/tests/TEST_STRATEGY.md:76 は「AST は対象外」＝別義。除外した）
```

**陽性対照（対で置いた）**: 同じ走査器・同じ pathspec で

```console
$ git grep -c "合成監視" -- . ':!src/ai-stock-trading'   → 39 ファイルが非ゼロ
$ git grep -c "対象外"   -- deploy docs scripts          → 130 ファイル超が非ゼロ
```

**非ゼロが返るため、上の絞り込みが 0 件でないことは走査の成功であって偶然ではない。**

🔴 **記憶で挙げていない**（本リポジトリの規則 9）。**指示は Grafana の 2 ファイルが写しを持つ可能性を
挙げていたが、走査の結果それらは繰り延べの注記を持っていなかった** —— 代わりに
**指示が挙げていなかった `deploy/local/observability/prometheus.yaml` が写しを持っていた。**

#### 母集合 A の内訳と扱い

| ファイル | 行 | 扱い |
| --- | --- | --- |
| `deploy/prometheus/alerts.yml` | 91-93 / 126-128 | ✅ **直す**（正本） |
| `deploy/local/observability/prometheus.yaml` | 87 | ✅ **直す**（経路 B の inline 写し） |
| `docs/operations/operations.md` | 713-714 / 1016-1017 | ✅ **直す** |
| `deploy/grafana/provisioning/alerting/slo-alerts.yaml` | — | ⛔ **注記を持たない**（対象経路の選び方を `alerts.yml` へ委譲していると本文に明記）。ただし **ルールの 1:1 のため新ルールの追加は要る** |
| `deploy/local/observability/grafana.yaml` | — | 同上（`slo-alerts.yaml` の inline 写し。バイト一致が要求される） |
| `.ai-context/adr/IADR-0354` / `IADR-0370` / `IADR-0378` | 244 / 64 / 46 | ⛔ **書き換えない。凍結記録である**（`traceability.repo.md`「凍結の射程」。`.ai-context/adr/` の本文へ後付け注記をしない） |
| `.ai-context/specs/` 3 本（#1204 / #1202 / #1203） | — | ⛔ **書き換えない。** 他 issue の確定済み作業仕様書である |

### 走査 2 —— 本 PR が `ADR-0079` を引くことで、同一文書内で偽になる記述（母集合 B・規則 10）

**規則 10 は「是正のたびに、この変更で新たに誤りになる自分の記述を引き直す」ことを求める。**
本 PR は `ADR-0079` を出典に引くため、**同じ文書の中で「頻度・費用の上限は計画側で未確定」と
書いている箇所が、その瞬間に自己矛盾する。**

```console
$ git grep -nE "計画側で未確定|裁定待ち|未定と残し|上限は定めていない|上限が未定" -- deploy docs scripts
```

| ファイル | 行 | 扱い |
| --- | --- | --- |
| `docs/operations/operations.md` | 833 / 834 / 842 / 1010-1015 | ✅ **直す**（同一文書内の自己矛盾） |
| `docs/observability/synthetic-traffic-exclusion.md` | 88-92 | ✅ **直す** |
| `deploy/local/synthetic-monitor/README.md` | 35 / 76 / §頻度と費用の上限 | ✅ **直す** |
| `deploy/local/synthetic-monitor/synthetic-monitor.yaml` | 53-58 | ✅ **直す** |
| `deploy/local/synthetic-monitor/probe.js` | 13-16 / 32 | ✅ **直す** |

#### 母集合 B から**除外した**もの（理由つき）

| 除外したもの | 理由 |
| --- | --- |
| **LLM 月次予算のしきい値が「計画側で未確定」**（`docs/observability/llm-usage-and-cost-metrics.md:136` / `docs/operations/llm-cost-monthly-review-runbook.md:30,114` / `docs/operations/operations.md:635,995` / `deploy/grafana/provisioning/dashboards/llm-usage.json:29` / `deploy/local/observability/grafana.yaml:96`） | 🔴 **別件である。** `ADR-0079` 決定 2 が確定させたのは**合成監視の**費用の扱いだけで、**計画 決定 41 の月次予算のしきい値は未確定のまま**である。同 ADR も「絶対額のしきい値は置かない」原則を **05_observability-ops の既存裁定に揃える**と書いており、覆していない。**直すと嘘になる。** |
| **他画面・他機能の「裁定待ち」**（`docs/screens/SC-01,03,05,07,08,10,18,21` / `docs/data/data-source.md` / `docs/how-to/session-handoff.md` / `docs/tests/SC-21` / `scripts/verify-oidc-edge-flow.sh`） | 無関係の別裁定である |
| `.ai-context/adr/IADR-0378` の「裁定待ち」（139-151 / 183-187） | **凍結記録**。当時の判断としてそのまま残す（現況は本仕様書と live 文書の側が持つ） |
| `scripts/scripts.repo.test.js:3326-3357` の「裁定待ち」 | #1195 / ADR-0069 の別件の追随検査である |

### 走査 3 —— 計画 ADR レンジの追随（母集合 C）

本 PR は `ADR-0079` を trace ブロック・コミット件名から引く。**`.claude/rules/traceability.repo.md` の
宣言レンジは `ADR-0001..0077` であり、`ADR-0079` は「計画レンジに実在しない」として
`check-trace-blocks.js` / `check-commit-messages.js` が落とす。**

```console
$ gh api "repos/endazon/project-planning/contents/projects/microservices-platform/07_adr" \
    --jq '[.[].name | select(test("^ADR-"))] | length'
81
$ gh api ... --jq '[.[].name | select(test("^ADR-")) | .[4:8]] | sort | first, last'
0001
0081
```

**件数 81 と両端 `0001` / `0081` が一致するため連番（欠番なし）である**（別紙が 6 回目までに確立した
数え方をそのまま使った）。**レンジを `ADR-0001..0081` へ引き直す**（7 回目）。

## 設計

### 🔴 実装 ADR（IADR）を新設しない —— 理由と、採番の制約

**本作業で新しい設計判断は生じていない。** 下の 3 点はいずれも**既存の記録から導かれる適用**である。

| 論点 | 既に決まっている場所 |
| --- | --- |
| `/analysis/ask` 系を `absent` の対象へ入れること | `ADR-0079` §結果（「`absent()` 系の警報が低頻度経路にも及ぶ。決定 3 の対象外だった `/analysis/ask` 系が対象へ入る」） |
| 初回トークンの系列は入れないこと | `ADR-0079` §統制と現在の実現手段（「LLM を呼ぶ合成が走っていないため評価対象が生まれない」）＋ `IADR-0378` 決定 7 ＋ `ADR-0076` 理由（恒常発火の回避） |
| 系列ごとに 1 件・名前は `…SeriesAbsent`・`for: 5m` / `warning` | `IADR-0370` 決定 2・3・5 |
| 窓は `absent()` か `absent_over_time()` か | `IADR-0370` 決定 6（既存へ揃える）＋ `IADR-0389` 決定 5（**周期駆動なら 2 周期ぶんの窓**）。生産者の周期は `ADR-0079` 決定 1 が確定させた値（60 秒 / 60 分）で決まる |
| opt-in オーバーレイが当たっていないクラスタで真になること | `IADR-0370` §実測 A（「クラスタ再作成中に本ルールが鳴るのは誤報ではない。そのとき評価対象は本当に無い」）と同型 |

🔴 **加えて、採番の制約が「作らない」を後押しした。** `.ai-context/adr/` の現在の最大は `IADR-0396` で、
**`IADR-0397` は並行する別の作業が確保している。** `IADR-0398` を取ると **`0397` が欠番**になり、
`node scripts/check-adr-numbering.js` の判定 2（`0000` から最大まで連続）が落ちる。
規約（`traceability.md`「採番衝突時の改番手順」）も「**後発は次の空き番号へ改番し、欠番を作らない**」である。
**判断の実体を本仕様書とアラート定義のコメントへ書き、番号は取らない。**

### 決定の要点

1. **入れるのは 1 件だけである** —— `RagLatencyP95High` が見る HTTP 系列に対する `RagLatencySeriesAbsent`。
2. **`RagFirstTokenP95High` の系列（`rag_answer_first_token_duration_seconds_bucket`）には入れない。**
   `AllowLlmEgress` の既定が `false` である限り `token` イベントが 1 件も出ず、
   **計器は設計どおり系列を持たない。** ここへ `absent` を置くと**恒常発火**になり、
   `ADR-0076` 理由が却下した案 B そのものになる。
3. **窓は `absent()`（既定 5 分 lookback）である。** 生産者はプローブであり、
   **配備マニフェストの `PROBE_INTERVAL_SECONDS` は `60`**（`ADR-0079` 決定 1 の確定値と一致）。
   `60 秒 × 5 = 5 分` の余裕があり、平常時に真にならない。
   **`absent_over_time([2h])` は `knowledge-health-producers` 群が周期 1 時間の生産者に対して使う形**であり、
   本件（60 秒）には過大である。

### 追加するルール（3 系統に同内容）

| 系統 | ファイル | 形 |
| --- | --- | --- |
| Prometheus（compose の正本） | `deploy/prometheus/alerts.yml` | `- alert: RagLatencySeriesAbsent` / `expr: absent(...)` / `for: 5m` / `severity: warning` |
| Grafana provisioning | `deploy/grafana/provisioning/alerting/slo-alerts.yaml` | `uid: slo-rag-latency-series-absent` / `noDataState: OK` / `evaluator: gt 0` |
| k8s（経路 B） | `deploy/local/observability/grafana.yaml`（inline・**バイト一致**）と `deploy/local/observability/prometheus.yaml`（inline・要約版） | 同上 |

```promql
absent(http_server_request_duration_seconds_bucket{job="microservices-platform.aianalysis-service", http_route="/analysis/ask"})
```

**`…_bucket` を見る**（`SearchLatencySeriesAbsent` と同じ）。`RagLatencyP95High` が
`histogram_quantile` の入力に使うのはこの系列であり、**支える SLO が実際に読む系列と一致させる。**

## 受け入れ基準

- [ ] `RagLatencySeriesAbsent` が Prometheus 版・Grafana 版・k8s inline の 3 系統に同内容で入っている
- [ ] 命名・`for:` ・ラベル・`description:` の書き方が既存の `…SeriesAbsent` 3 件と揃っている
- [ ] `rag_answer_first_token_duration_seconds_bucket` の `absent` は**入れておらず**、
      入れない理由（恒常発火の回避と `ADR-0079` フォローアップ 2 への従属）が注記に残っている
- [ ] 母集合 A の live 文書 5 か所すべてで、失効した繰り延べの注記が
      **「何が変わったのか」を書いた注記へ置き換わっている**（黙って消していない）
- [ ] 母集合 B の 5 ファイルで「頻度・費用は計画側で未確定」が `ADR-0079` の確定値へ置き換わっている
- [ ] `node scripts/check-grafana-alerting.js` が緑（ルール数 **11 → 12** の 1:1 が保たれている）
- [ ] `node scripts/check-grafana-provisioning-parity.js` / `check-trace-blocks.js` / `check-doc-links.js` /
      `check-adr-numbering.js` / `check-doc-updated.js` / `check-commit-messages.js` が緑

### 🔴 走査ではなく計算し直した導出値（規則 10）

**ルール総数は「9 件」と各所に書かれていたが、実測すると着手時点で 11 件だった。**
`#1246` が `knowledge-health-producers` 2 件を足したときに追随されず、**2 世代ぶん腐っていた。**
本 PR で 12 件になる。**記述を走査して置換するのではなく、実体を数え直して書いた。**

```console
$ grep -cE "^\s*-\s*alert:" deploy/prometheus/alerts.yml            → 12
$ grep -cE "^\s*(-\s*)?title:" deploy/grafana/provisioning/alerting/slo-alerts.yaml → 12
$ grep -cE "^\s*-\s*alert:" deploy/local/observability/prometheus.yaml → 10
```

🔴 **3 つ目が 10 なのは乖離である**（`knowledge-health-producers` の 2 件が経路B へ写されていない）。
**#1246 の射程なので本 PR では足さず、受容として当該ファイルと運用仕様書へ記録した。**

## テスト方針

**本作業に単体テストの写像先は無い**（変更は配備設定と文書に閉じ、アプリケーションコードを 1 行も変えない。
#1202 と同じ性質である）。担保は上記の機械検査器と、稼働クラスタでの実測（§未決事項）である。

## 計画書との差異

- 差異: **なし。** `ADR-0079` §結果 が「`/analysis/ask` 系が対象へ入る」と明示した内容を実装する。
  ただし**同 ADR の フォローアップ 2・3 は本 PR の射程外**であり、その分だけ
  `NFR-02` の SLO は評価対象を持たないままである（§未決事項）。

## 未決事項

1. 🔴 **稼働クラスタでの実測が取れていない。** 本作業の変更は配備設定であり、
   **`RagLatencySeriesAbsent` が「合成が回っているとき鳴らず、止めると鳴る」ことを実測するには、
   除外を含むイメージが配備され、かつ opt-in オーバーレイが当たったクラスタが要る。**
   IADR-0378 §影響・残るもの が記録したとおり、そのイメージは焼き直されていない。
   **測れなかったものは測れなかったと記録する。**
2. **`ADR-0079` フォローアップ 2・3 は別 issue である。** 60 分間隔の SLO 評価用プローブと、
   `RagFirstTokenP95High` の評価窓の実測は、**本 PR の宣言ファイル領域を超える**
   （`src/` の構成キー・稼働実測・計画への環流を伴う）。
3. **`deploy/local/synthetic-monitor` は依然 opt-in である。** 当てていないクラスタでは
   `RagLatencySeriesAbsent` は真になる。**これは誤報ではなく「評価対象が本当に無い」状態である**
   （IADR-0370 §実測 A がクラスタ再作成中について同じ整理をしている）が、
   **既定の起動器へ入れる条件（除外を含むイメージ）は `ADR-0079` フォローアップ 1 に残る。**
