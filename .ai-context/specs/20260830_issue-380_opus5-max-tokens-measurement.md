---
title: 作業仕様書 — #380 Opus 5 の出力トークン実測（max_tokens 4096 の再調整・429 の確認）
type: spec
status: done
related_ids:
  - FR-11
  - NFR
  - ADR-0006
  - ADR-0025
  - ADR-0038
  - ADR-0044
  - IADR-0101
  - IADR-0110
  - IADR-0210
  - IADR-0212
  - IADR-0225
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
related_specs: []
issue: "380"
---

# 作業仕様書 — #380 Opus 5 の出力トークン実測

## 結論（先に書く）

**測れなかった。** よって **`max_tokens` 4096 は変更しない**（推定で動かさない）。
IADR-0101 のフォローアップ 1・2 は未消化のまま残す。本書は「何を叩いて何が返ったか」と、
**測定を可能にするために足りないもの**を記録する。

## 対象範囲（母集合を自分で引いた結果）

### 1. `max_tokens 4096` の設置箇所

引き方: 誤りの側ではなく「値そのもの」で引き、拡張子で絞らず `src/` 全体を走査した。

```
grep -rn "4096" --include=*.cs --include=*.json --include=*.yaml --include=*.yml src/
```

| # | 位置 | 役割 |
| --- | --- | --- |
| 1 | `Platform.Shared.Contracts/Dtos/CompletionDto.cs:39` `CompletionApiRequest.MaxTokens = 4096` | HTTP 経路（`/complete`・`/complete/stream`）の既定 |
| 2 | `LlmGateway/Domain/Ports/ILlmProvider.cs:28` `CompletionRequest.MaxTokens = 4096` | プロバイダ直呼び（内部経路）の既定 |
| 3 | `AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs:247, 375` | 明示指定 2 呼び出し |

**除外したもの**:

- `Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs:110`（`new byte[4096]` ＝ ストリーム読み出しバッファ。無関係）
- `LlmGateway/Common/Observability/LlmCompletionMetrics.cs:62`（Histogram のバケット境界。`max_tokens` を**読むための**値であり設定値ではない）
- `LlmGateway/Tests/*`（1〜3 を固定するテスト。値を変えるなら追随するが、母集合そのものではない）
- `src/ai-stock-trading`（submodule・本リポジトリでは修正不可。IADR-0101 の追記により消化済み）

issue #380 本文の「対象は 3 箇所」と一致した（`RagOrchestrator` は 1 箇所と数えて 3）。

### 2. otel collector の設定ファイル（副産物として引いた母集合）

拡張子で絞らず、パスから引いた。

```
git grep -l "exporters:"
  → .ai-context/superpowers/plans/2026-06-26-P0-foundation.md   （凍結記録。除外）
    deploy/local/infra/otel-collector.yaml
    deploy/local/observability/otel-collector-forward.yaml
    deploy/otel-collector-config.yaml
grep -rln "address: 0.0.0.0:8888" deploy/ scripts/
  → deploy/local/infra/otel-collector.yaml
    deploy/otel-collector-config.yaml
```

**3 件中 2 件しか `service.telemetry.metrics.address` を持たない。** 欠けているのは
`deploy/local/observability/otel-collector-forward.yaml` ＝ **OBSERVABILITY=1 のとき実際に走る側**である。
`deploy/tempo.yaml` / `deploy/local/observability/tempo.yaml` は `otlp:` に一致するが Tempo 自身の
receiver 設定であり collector ではないため除外した。

## 実測（2026-08-30・稼働中の Rancher Desktop k3s v1.35.4+k3s1）

### 手順

```
kubectl -n platform-infra port-forward svc/prometheus 19090:9090
```

### (a) `llm_*` 系列は 1 つも存在しない

```
$ curl -s ".../api/v1/label/__name__/values" | tr ',' '\n' | grep -ci llm
0

$ curl -s --get ".../api/v1/series" --data-urlencode 'match[]={__name__=~"llm.*"}' \
        --data-urlencode "start=<now-40d>" --data-urlencode "end=<now>"
{"status":"success","data":[]}
```

`#380` が必要とする PromQL は全て空ベクタを返した。

```
sum(increase(llm_completion_output_tokens_count[7d]))                         → []
histogram_quantile(0.95, sum by (le) (rate(llm_completion_output_tokens_bucket[7d]))) → []
sum by (le)  (increase(llm_completion_output_tokens_bucket[7d]))              → []
sum by (llm_result)      (increase(llm_completion_total[7d]))                 → []
sum by (llm_stop_reason) (increase(llm_completion_total[7d]))                 → []
sum by (llm_purpose, llm_token_type) (increase(llm_tokens_total[7d]))         → []
sum by (llm_purpose)     (increase(llm_cost_total[7d]))                       → []
sum(increase(llm_pricing_unpriced_total[7d]))                                 → []
```

### (b) なぜ空か —— 5 つの独立した理由

1. **`/complete` への実トラフィックがゼロ。** llmgateway-service Pod（起動 06:34Z、5 時間超）の
   ログ 15,369 行は `health/live` `health/ready` `internal/introspection` だけで、補完呼び出しは 0 件。
2. **collector が転送していない。** 稼働中の `otel-collector-config` は metrics/traces/logs の
   3 パイプラインとも `exporters: [debug]` のみ（＝ `deploy/local/infra/otel-collector.yaml` の
   fail-safe 構成）。`otelcol_receiver_accepted_metric_points` = **739,069** に対し
   `otelcol_exporter_sent_metric_points{exporter="debug"}` = **739,069**。
   **他の exporter は 1 つも存在しない**（＝受け取った全点が破棄されている）。
3. **Prometheus の scrape 対象は collector 自身 1 つだけ。** `/api/v1/targets` の activeTargets は
   `http://otel-collector:8888/metrics`（job=otel-collector）の 1 件のみ。アプリの系列は
   remote-write でしか入らないが、上記 2 によりその経路が死んでいる。
4. **稼働 image に計器が無い。** `/app/LlmGateway.Api.dll`（mtime **Aug 16 10:19**）を UTF-16 剥がして
   走査すると、`llm.completion.total` と 6 属性は在るが、**`llm.completion.output_tokens` は無い**
   （IADR-0212 / #786 は Aug 16 より後）。`llm.tokens.total` / `llm.cost.total` /
   `llm.pricing.unpriced.total`（#443）も無い。**#380 が読むはずの Histogram が焼かれていない。**
5. **Prometheus に PVC が付いていない。** Deployment の volumes は configMap 1 本だけで、
   `prometheus-data`（Bound・5Gi・14d）は mount されていない。実データは Pod 起動（04:19Z）以降の
   7.5 時間分しかなく、再起動で全消失する（IADR-0210 / #787 の永続化オーバーレイが当たっていない）。

### (c) 429（レート制限枠）— メトリクスでは原理的に分からない

トラフィックが在ったとしても答えられない。`Features/Completions/Complete/Endpoint.cs` は
**上流の HTTP ステータスを持たない軸へ潰している**。

- フォールバックした呼び出し → `llm.result="fallback"`（ログにのみ `{Status}` が出る）
- それ以外の失敗（**429 を含む**）→ `llm.result="upstream_error"`（**ステータスは構造化されず消える**）

`LlmFallbackPolicy` は 429 を「フォールバックさせない」と正しく分類している（ADR-0038 決定 4 / #863）が、
**分類した結果をメトリクスへ残していない**。`LlmCompletionMetrics` に HTTP ステータスのタグは無い。
唯一の代替は Loki のログだが、Loki は `/loki/api/v1/labels` が空を返す（logs パイプラインも `debug` のみ）。

### (d) 併せて観測した、稼働クラスタと Grafana の乖離

- `grafana-dashboards/llm-usage.json` の 11 個の PromQL は `llm_cost_total` / `llm_tokens_total` /
  `llm_pricing_unpriced_total` を読むが、**稼働 image はそのどれも emit しない**（上記 (b)-4）。
- 同ダッシュボードに **出力トークン Histogram のパネルが 1 枚も無い**。#380 が読むべき計器
  （IADR-0212）が可視化されていない。

## 判断

- **`max_tokens` は変更しない。** 4096 が過大か過少かを示す観測が 1 点も無く、
  変えれば「実測前の出発値」を「実測を騙る別の出発値」に置き換えるだけになる。
- IADR-0101 のフォローアップ 1・2 は **`blocked` のまま**維持し、下記が揃うまで再挑戦しない。

## 測定を可能にするために足りないもの

| # | 足りないもの | 手当て |
| --- | --- | --- |
| 1 | 転送構成での稼働（`OBSERVABILITY=1 PERSIST=1` での再適用） | 運用手順。ただし転送構成には #546 の `telemetry.metrics.address` が無い（新規 issue） |
| 2 | `develop` 相当の image（`llm.completion.output_tokens` を含む） | 手で再ビルド。CD 不在は #442 の射程 |
| 3 | 実 API キーと、人の許可を得た負荷投入 | `Llm__ApiKey` は空。**費用が出るため人の判断が要る**（#380 の `blocked` の実体） |
| 4 | 429 を他の失敗と区別できる軸 | 新規 issue（`llm.result="upstream_error"` に上流ステータスの軸が無い） |
| 5 | Prometheus の永続化（PVC mount） | IADR-0210 / #787 のオーバーレイ適用（運用手順） |

## 変更したもの

- 本作業仕様書
- `.ai-context/adr/IADR-0101_default-model-opus-5.md` の フォローアップ 1・2 へ日付つき追記
  （`.claude/rules/traceability.repo.md`「凍結の射程」を確認したうえで実施。同ファイルは既に
  2026-08-07・2026-08-10 の同型の追記を持ち、live な権威文書として追記が許される側である）

**コードは 1 行も変更していない。**
