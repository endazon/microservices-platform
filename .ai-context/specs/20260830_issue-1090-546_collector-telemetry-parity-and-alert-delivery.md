---
title: 作業仕様書 — 転送構成の自己テレメトリ宣言を揃え、アラートが Alertmanager へ届くことを実測する（#1090 / #546 残）
type: spec
status: done
related_ids:
  - NFR
  - NFR-21
  - SC-10
  - FR-11
  - ADR-0006
  - ADR-0044
  - IADR-0164
  - IADR-0165
  - IADR-0265
  - IADR-0304
  - IADR-0312
author: claude
created: 2026-08-30
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (§LLM 費用の上限アラートと暫定の統制・決定 39〜42／§リスク・未決事項)
related_specs:
  - "20260830_issue-546_alertmanager.md"
  - "20260810_issue-665_grafana-alerting.md"
  - "20260810_issue-546_llm-cost-monthly-review.md"
issue: "#1090"
---

# 作業仕様書 — #1090 と #546 の残作業

## 射程（2 件を 1 PR にまとめる理由）

**同じ資源（経路B の collector 設定 ＋ Alertmanager 経路）を触り、片方の受け入れ基準がもう片方の前提になる。**

- #1090 の受け入れ基準 3「転送構成を apply → rollout restart した状態で `up{job="otel-collector"} == 1` を実測」
- #546 の残 3「意図的に閾値を割ってアラートが Alertmanager へ実際に届くことを実測」

後者の唯一の実用ルール `OtelCollectorDown` は `up{job="otel-collector"}` を見ている。**転送構成のまま
scrape が落ちていたら、届いたアラートが「仕込んだ故障」なのか「#1090 の欠落」なのか区別できない。**
先に #1090 を塞いでからでないと #546 の実測が成立しない。`IADR-0139` の束ね例外ではなく、
**受け入れ基準が連鎖している 1 つの作業**として扱う。

## 母集合（着手時に自分で引いた。issue 本文の列挙は転記していない）

### 軸 1 — collector 設定の全体（誤りの側＝「持っていない側」から引く）

```
$ git grep -lI "^\s*exporters:"
.ai-context/superpowers/plans/2026-06-26-P0-foundation.md
deploy/local/infra/otel-collector.yaml
deploy/local/observability/otel-collector-forward.yaml
deploy/otel-collector-config.yaml
```

### 軸 2 — 宣言を持っている側から引く（差集合を取る）

```
$ git grep -lI "0.0.0.0:8888"
.ai-context/adr/IADR-0304_alertmanager-deployment-and-null-receiver.md
.ai-context/specs/20260830_issue-380_opus5-max-tokens-measurement.md
deploy/local/infra/otel-collector.yaml
deploy/otel-collector-config.yaml
```

### 軸 3 — キー名から引く（値の書き方に依存しない）

```
$ git grep -nI "telemetry:" -- deploy/
deploy/local/infra/otel-collector.yaml:31
deploy/otel-collector-config.yaml:33
```

### 軸 4 — ポート番号から引く（設定ファイル以外も取る。拡張子で絞らない）

```
$ git grep -lI "8888"
deploy/docker-compose.yml / deploy/local/infra/otel-collector.yaml
deploy/local/observability/prometheus.yaml / deploy/otel-collector-config.yaml / deploy/prometheus.yml
（他は .ai-context/ の凍結記録）
```

**3 軸とも同じ 1 件を指した**: `deploy/local/observability/otel-collector-forward.yaml`。
軸 4 で `deploy/local/observability/otel-collector-forward.yaml` が出てこないこと自体が欠落の裏取りである。

**除外したものと理由**:

- `.ai-context/superpowers/plans/2026-06-26-P0-foundation.md` — 凍結記録。本文を後から書き換えない（`traceability.repo.md`）。
- `.ai-context/adr/*` `.ai-context/specs/*` — 同上（確定済み記録）。ただし**前提が解消した旨の日付つき追記**は別（下の軸 5）。
- `deploy/local/infra/otel-collector.yaml` の Deployment / Service — **転送オーバーレイは ConfigMap
  しか差し替えない**（`kustomization.yaml` の `resources` は `otel-collector-forward.yaml` を含むが、
  同ファイルは ConfigMap 1 個だけ）。したがって `containerPort: 8888` と Service の 8888 は
  **infra 側の宣言がそのまま効く**。issue が挙げた「`containerPort: 8888` / Service port 8888 相当が無い」は
  **オーバーレイの構造上そもそも重複しない** —— 実測（下記）で確認した。

### 軸 5 — #546 段 1 で新たに誤りになった自分の記述（`traceability.repo.md` 規則 10）

Alertmanager は 2026-08-30（PR #1068 / `1ac1ecb3`）に**配備済み**で、`alertmanagers.targets` は
compose・経路B の 2 か所とも埋まっている。**「未配備」「targets が空」と書いている生きた記述**を引き直す。

```
$ git grep -nI "未配備\|targets.*空\|自動検知が無い" -- docs/ deploy/ scripts/ | grep -vi kiali
```

| 箇所 | 何が誤りになったか |
| --- | --- |
| `docs/operations/operations.md` §監視・アラート（`未配備でもルール評価は行われ`） | 配備済み |
| 同 §監視・アラート（`Alertmanager は未配備であり`） | 配備済み |
| 同 §監視・アラート §適用範囲（`現状 dev（docker-compose）にのみ配線`） | 経路B にも配線済み |
| 同 §未決事項（`現状はターゲット未設定でルール評価のみ。compose・k8s の 2 か所とも空`） | 2 か所とも埋まっている |
| 同 §未決事項（`LLM 費用の自動検知: 無い（Alertmanager 未配備）`） | 無いのは事実だが**理由が誤り** |
| `docs/operations/llm-cost-monthly-review-runbook.md` ⚠前提の表 | 同上 |
| `docs/observability/llm-usage-and-cost-metrics.md`（`通知基盤が未配備`） | 配備済み |
| `docs/functional/FR-01_data-source-catalog.md`（`Alertmanager への配線は未配備`） | 配備済み |
| `deploy/grafana/provisioning/dashboards/llm-usage.json` テキストパネル | 配備済み |
| `deploy/local/observability/grafana.yaml` の inline（**同内容。パリティ検査あり**） | 同上 |

**この軸を引かなければ「配備した」と「配備していない」が同じリポジトリに同居し続けた。**
計画 決定 40（統制を定める記述には現在の実現手段を併記する）が名指しした型そのものである。

## 判断 1 — #1090 を単一情報源にできるか

**できない。理由を書く（issue の要求どおり）。**

3 つの設定は**排他的な別配備の全体設定**であり、共有できる形が無い。

| 経路 | ファイル | 共有できない理由 |
| --- | --- | --- |
| compose | `deploy/otel-collector-config.yaml` | bind mount。kustomize の管理外 |
| 経路B 既定（fail-safe） | `deploy/local/infra/otel-collector.yaml` の ConfigMap | 転送構成と**同名 ConfigMap を上書きし合う**排他関係 |
| 経路B 転送（opt-in） | `deploy/local/observability/otel-collector-forward.yaml` | 同上 |

- **kustomize の strategic merge patch では届かない。** ConfigMap の `data` は**文字列 1 個**であり、
  YAML の内側へ patch を当てられない（`data.config.yaml` は不透明な文字列）。
- **`configMapGenerator` の共有ファイル化も採れない。** kustomize は root 外のファイルを参照できず、
  compose 用の設定を k8s の overlay から読めない（`prometheus.yaml` / `grafana.yaml` が
  既に同じ理由で inline 二重管理を選んでいる）。
- **collector の `--config` 多重指定でマージする**案は、1 行のために起動引数・追加 ConfigMap・
  マウントを増やす。**dev 限定の設定 1 行に対して過剰**であり、`CLAUDE.md` の「過剰な抽象化を行わない」に反する。

→ **単一情報源にはできない。代わりに乖離を機械で止める**（下の判断 2）。

## 判断 2 — 検査器を足す（**同型の事故が 2 回起きた**ので条件を満たす）

`CLAUDE.md`: 「検査器・規約の追加は**同型の事故が 2 回起きたら**を条件とする（1 回目は記録に留める）」。

| 回 | 事故 | 記録 |
| --- | --- | --- |
| 1 回目 | 経路B の collector に `containerPort: 8888` / Service 8888 / `telemetry.metrics.address` が無く、`up` が恒常 0 だった | `IADR-0304` §配備して初めて分かったこと（「**検査器は足さない**」と明記） |
| 2 回目 | **転送構成にだけ** `telemetry.metrics.address` が無い（#1090） | 本仕様書 |

**2 回目なので足す。** `scripts/check-collector-self-telemetry.js` を新設し、
**collector 設定を列挙せず走査で発見して**、全件が同じ宣言を持つことと、
Prometheus の scrape 対象ポートと一致することを見る。

## 判断 3 — LLM コスト予算の上限アラートを置くか → **置かない（(b) を採る）**

🔴 **計画が明示的に禁じている。** `05_observability-ops` §LLM 費用の上限アラートと暫定の統制
（**利用者裁定 2026-08-08**・`fixed`）:

> **月次予算の金額（しきい値）は定めない。実測を待って確定する。** 稼働実績が無い段階で置いた絶対額は、
> 超過しても過少でも判断の根拠にならない。

同書 §リスク・未決事項:

> **確定の前提は、`IADR-0110` §結果 フォローアップ 2 のトークン消費量・金額換算が稼働し、
> **実績が数か月分そろうこと**である。

実測（下記 §実測 3）と合わせて、置かない根拠は 4 本ある。

1. **計画（fixed・裁定済み）が禁じている。** 実装が数字を決めると、それが既成事実として計画へ逆流する。
2. **`llm_cost_total` の系列が 1 本も無い**（実測。全保持期間でゼロ件）。
3. **経路B の Prometheus では月次スケールの規則が原理的に評価できない** ——
   保持は `--storage.tsdb.retention.time=7d` であり、**稼働クラスタは PVC を持たない**（実測）。
   `[30d]` の窓を持つ規則は dev では常に空になる。
4. **計画 決定 39 が「併存させない」と定めている。** 月次の手動確認（`IADR-0164`）が生きている間に
   部分的な自動統制を足すと、**どちらが正かを決める根拠が無くなる**（裁定 Q26 が避けた形と同じ）。

**`absent()` で「系列が無いこと」を検知する案（(a)）も採らない。** いま置けば**恒常的に発火し続ける**。
`IADR-0304` 決定 3 が防ごうとしている「既知の誤報」を、配備の翌日に自分で作ることになる。

→ **予算アラートは別 issue へ分ける（#1111）。** #546 では**届く経路の実証**を門にする。
分けた issue には、着手前に確かめるべき前提 3 つ（しきい値の確定・費用の計器を含むイメージの配備・
月次スケールを評価できる保持）を**受け入れ基準として書いた** —— 番号を移すだけにしない。

## 受け入れ基準

| # | 基準 | 起点 |
| --- | --- | --- |
| 1 | `deploy/local/observability/otel-collector-forward.yaml` が `service.telemetry.metrics.address: 0.0.0.0:8888` を持つ | #1090 |
| 2 | `git grep -lI "0.0.0.0:8888" -- deploy/` が 3 件返す | #1090 |
| 3 | 転送構成を apply → `rollout restart` した状態で `up{job="otel-collector"} == 1` を実測 | #1090 |
| 4 | 新設検査器が、宣言を落とした変異を検出する（自己試験つき） | 判断 2 |
| 5 | **意図的に閾値を割り、`/api/v2/alerts` に発火した JSON が現れる**ことを実測 | #546 残 3 |
| 6 | 軸 5 の「未配備」記述を全数是正し、`check-grafana-provisioning-parity.js` が緑 | #546 残（決定 40） |
| 7 | 予算アラートを分けた判断を IADR と新 issue に残す | 判断 3 |

## 実測（2026-08-30・稼働中の Rancher Desktop k3s `v1.35.4+k3s1` / `platform-infra`）

### 実測 0 — 着手時のクラスタの状態（**マニフェストではなく実物を見る**）

- collector は **fail-safe 構成**（`exporters: [debug]`）で稼働。転送オーバーレイは未適用だった。
- Alertmanager / Grafana / Loki / Tempo / Prometheus は稼働中。
  `alertmanagers.targets` は `['alertmanager:9093']`、Prometheus の
  `/api/v1/alertmanagers` は `activeAlertmanagers` を 1 件返す。
- `up{job="otel-collector"} == 1`（#546 段 1 の是正が効いている）。
- 転送オーバーレイは **ConfigMap 1 個だけ**を差し替える。`containerPort` / Service の 8888 は
  `deploy/local/infra/otel-collector.yaml` の宣言がそのまま効く（＝ issue の指摘のうち
  ポート 2 点は**構造上そもそも重複しない**）。

### 実測 1 — #1090 の是正後、転送構成で待受が明示される

```
$ kubectl apply -k deploy/local/observability
configmap/otel-collector-config configured
$ kubectl -n platform-infra rollout restart deploy/otel-collector
$ kubectl -n platform-infra logs deploy/otel-collector | head
info service@v0.102.0/telemetry.go:96  Serving metrics  {"address": "0.0.0.0:8888", "level": "Normal"}
...
warn localhostgate/featuregate.go:63   The default endpoints for all servers in components will
     change to use localhost instead of 0.0.0.0 in a future version.
```

🔴 **collector 自身が、#1090 が指摘した壊れ方を警告として出している。** 版を上げれば既定は
`localhost` へ倒れる。**明示があるかどうかがそのまま生死を分ける。**

```
$ kubectl -n platform-infra exec deploy/prometheus --     wget -qO- 'http://localhost:9090/api/v1/query?query=up{job="otel-collector"}'
{"status":"success","data":{"result":[{"metric":{"instance":"otel-collector:8888",
 "job":"otel-collector"},"value":[1788092835.007,"1"]}]}}
```

**受け入れ基準 3 を満たした。**

### 実測 2 — 転送が効いていることの裏取り（`up` だけでは足りない）

`up == 1` は「collector の 8888 が生きている」ことしか言わない。**転送そのもの**を確かめる。

```
$ ... query=count(http_server_request_duration_seconds_count)   → 77
```

**アプリのメトリクスが Prometheus へ届いている**（fail-safe 構成では 0 件だった）。

### 実測 3 — `llm_cost_total` は存在しない。**理由は「呼ばれていない」ではない**

```
$ ... query=count({__name__=~"llm.+"})   → 空（0 件）
```

系列が無い理由を、**稼働中のバイナリを直接見て**特定した（「まだ呼ばれていないだけ」なら
判断が変わるため）。.NET のアセンブリ文字列は UTF-16 なので NUL を落としてから引いた。

```
$ POD=llmgateway-service-76fb5476cd-nd27h
$ kubectl -n microservices-platform exec $POD -- sh -c 'tr -d "\000" < /app/LlmGateway.Api.dll > /tmp/s.txt
    for s in llm.completion.total llm.tokens.total llm.cost.total LlmUsageMetrics LlmCompletionMetrics; do
      printf "%s: " "$s"; grep -q "$s" /tmp/s.txt && echo PRESENT || echo ABSENT
    done'
llm.completion.total: PRESENT
llm.tokens.total:     ABSENT
llm.cost.total:       ABSENT
LlmUsageMetrics:      ABSENT
LlmCompletionMetrics: PRESENT
```

**リポジトリのソースには実装済みである**（`LlmUsageMetrics.cs`。2026-08-23 / #443）。
**稼働イメージが古い**（`MassTransit.dll` を含む＝ Wolverine 移行前）。

→ **判断 3（置かない）を裏付けた。** いま予算アラートを置いても、評価対象を 1 本も持たない。

#### ［2026-08-31 追記 / #1090］develop 取り込み後に再測した —— **この根拠は弱まった**

イメージが再ビルドされ、**稼働イメージは計器を持つようになった**（置き場も
`/app/LlmGateway.Api.dll` → `/app/LlmGateway.dll` へ変わっている）。

```
$ POD=llmgateway-service-796cf8f4dd-4m4vl
$ kubectl -n microservices-platform exec $POD -c llmgateway-service -- sh -c 'set -e
    tr -d "\000" < /app/LlmGateway.dll > /tmp/s.txt; wc -c < /tmp/s.txt; ...'
101763                        ← 陽性対照（走査結果が空でないこと）
llm.completion.total: PRESENT
llm.tokens.total:     PRESENT
llm.cost.total:       PRESENT
LlmUsageMetrics:      PRESENT
LlmGateway:           PRESENT  ← 陽性対照
```

🔴 **旧パスのまま測ると、`sh` の `cannot open ...` を見落として「全部 ABSENT」と読める。**
**陰性の結論には陽性対照を対で置く。**

**それでも `llm_*` の系列は 0 件のままである**（`count({__name__=~"llm.+"})` → 空。2026-08-31 実測）。
**理由が「計器が無い」から「まだ 1 度も呼ばれていない」へ変わっただけ**で、
**評価対象が無いことは変わらない。**

**判断 3 は変えない。** 4 本の根拠のうち弱まったのは (2) だけで、
**(1) 計画が明示的に禁じている**は単独で決定的である。

あわせて (3) を再測した —— **PVC は「無い」のではなく「あるのに繋がっていない」**。
`prometheus-data`（5Gi）は `Bound` だが、Deployment の `volumes` は ConfigMap 1 つだけである
（`--storage.tsdb.retention.time=7d` も据え置き）。**存在の確認で満足すると見落とす。マウントを見る。**

### 実測 4 — 🔴 **SLO ルール 5 件のうち 4 件は、そもそも評価対象を持たない**（本作業の射程外・別 issue）

転送を有効にして実データが流れる状態で突き合わせた。

| ルールが見ているもの | 実際に届いているもの |
| --- | --- |
| `http_server_duration_milliseconds_count`（**0 件**） | `http_server_request_duration_seconds_count`（**77 系列**） |
| ラベル `service_name`（値は `otelcol-contrib` のみ） | ラベル `job`（`microservices-platform.retrieval-service` 等 23 件） |
| ラベル `http_status_code` | ラベル `http_response_status_code` |
| しきい値の単位 ミリ秒（1500 / 5000） | 秒 |

実系列のラベル（実測）:

```
{__name__="http_server_request_duration_seconds_count", http_request_method="GET",
 http_response_status_code="200", http_route="/health/live",
 job="microservices-platform.retrieval-service", ...}
```

**動くのは `OtelCollectorDown` だけである。** 資源も原因も #1090 / #546 と別なので**別 issue へ分けた（#1110）**。
🔴 **ダッシュボード（`microservices-platform-overview.json`）も同じ名前を使っている** ——
つまり 5xx 率・p99・RAG レイテンシのパネルも空である。**アラートだけ直すと、運用者が空のグラフを見て
「異常なし」と記録する**状態が残るので、同 issue で同時に直すことを受け入れ基準にした。

### 実測 5 — 🔴 **意図的に閾値を割り、アラートが Alertmanager へ届くことを確かめた**（#546 残 3）

**並行セッションがクラスタを共有しており**、1 回目は 2 分の `for:` を待つ間に別の apply が
`replicas` を 1 へ戻して pending が消えた（**共有クラスタでの実測は 1 回では終わらない**）。
5 秒おきに 0 へ押さえ続けて再実施した。

```
$ kubectl -n platform-infra scale deploy/otel-collector --replicas=0   # 12:34:02Z 以降 up=0

# Prometheus 側（for: 2m の経過で pending → firing）
$ kubectl -n platform-infra exec deploy/prometheus -- wget -qO- 'http://localhost:9090/api/v1/alerts'
{"status":"success","data":{"alerts":[{"labels":{"alertname":"OtelCollectorDown",
 "instance":"otel-collector:8888","job":"otel-collector","severity":"critical"},
 "state":"firing","activeAt":"2026-08-30T12:34:02.387120232Z","value":"0e+00"}]}}

# Alertmanager 側（port-forward して /api/v2/alerts を直接読む）
$ kubectl -n platform-infra port-forward svc/alertmanager 19093:9093 &
$ curl -s http://127.0.0.1:19093/api/v2/alerts
[{"annotations":{"description":"otel-collector の scrape が 2 分間失敗。収集が停止している可能性。",
  "summary":"OTel Collector 到達不可（可観測性パイプライン断）"},
  "endsAt":"2026-08-30T12:40:02.387Z","fingerprint":"04edc81bc490381b",
  "receivers":[{"name":"default-null"}],"startsAt":"2026-08-30T12:36:02.387Z",
  "status":{"inhibitedBy":[],"silencedBy":[],"state":"active"},
  "updatedAt":"2026-08-30T12:36:02.397Z",
  "generatorURL":"http://prometheus-6bb9574cd4-8kx5q:9090/graph?g0.expr=up%7Bjob%3D%22otel-collector%22%7D+%3D%3D+0&g0.tab=1",
  "labels":{"alertname":"OtelCollectorDown","instance":"otel-collector:8888",
  "job":"otel-collector","severity":"critical"}}]
```

**`receivers: [{"name":"default-null"}]`** —— **届いた先は「どこへも送らない」受信先である**
（`IADR-0304` 決定 2）。**経路は生きている。人にはまだ届かない。** これが現在地である。

復旧（`--replicas=1`）後、`up` は 1 へ戻り `/api/v2/alerts` は `[]` になった。

### 実測 6 — クラスタは作業前の状態へ戻した

collector は 1 replica・**fail-safe 構成**（`exporters: [debug]`）。転送オーバーレイは
並行セッションの `deploy/local/infra` 適用で既に戻っていた（＝ opt-in の既定に一致する）。

## 検証

- `node scripts/check-deploy-manifests.js`（**kubeconform が PATH に無い**。結果は正直に報告する）
- `node scripts/check-grafana-alerting.js` / `check-grafana-provisioning-parity.js`
- `node scripts/check-collector-self-telemetry.js`（新設）+ `--self-test`
- `check-commit-messages` / `check-trace-blocks` / `check-doc-links` / `check-doc-updated` /
  `gen-knowledge-graph --check` / `check-adr-numbering`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
