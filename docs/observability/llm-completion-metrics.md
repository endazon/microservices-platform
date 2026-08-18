---
title: LLM 補完メトリクス（終了理由・拒否率）ログ・可観測性仕様書
type: observability-spec
status: in-progress
related_ids:
  - FR-11
  - NFR
  - UC-01
  - UC-02
  - ADR-0006
  - ADR-0010
  - ADR-0025
  - IADR-0104
  - IADR-0109
  - IADR-0110
  - IADR-0212
  - IADR-0225
  - ADR-0038
author: claude
created: 2026-07-28
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
related_specs:
  - "../adr/IADR-0212_llm-output-token-histogram.md"
  - "../adr/IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md"
  - "../adr/IADR-0110_llm-completion-stop-reason-metrics.md"
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../operations/operations.md"
---

# 可観測性仕様書: LLM 補完の終了理由（拒否率）

## 起点

- 起点 issue: [#395](https://github.com/endazon/microservices-platform/issues/395)
  （[[IADR-0104]] §フォローアップ 3 の消化）。実装判断は [[IADR-0110]]。
- 要求: FR-11（LLM 送信可否の統制）、NFR（可観測性・障害検出）。

## メトリクス定義

| 項目 | 値 |
| --- | --- |
| Meter 名 | `microservices-platform.llm-gateway`（サービス名と一致） |
| 計器 | `llm.completion.total`（`Counter<long>`・単位 `{completion}`） |
| 発行元 | `LlmGateway` の `/complete`・`/complete/stream`（**全終了経路**） |
| 収集経路 | OTel SDK → OTLP（`Otlp:Endpoint`）→ Collector → Prometheus（ADR-0006） |
| Prometheus 側の名前 | `llm_completion_total`（OTel の Prometheus 変換規則による） |

### 属性（すべて有限集合）

| 属性 | 値域 | 意味 |
| --- | --- | --- |
| `llm.result` | `sent` / `egress_denied` / `provider_missing` / `upstream_error` / `fallback` | **送信可否の軸**（FR-11 の `Sent` に対応）。`fallback` は #863 で追加（下記） |
| `llm.stop_reason` | `end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use` / `other` / `none` | **モデル側の終了理由**（`Sent` とは独立した軸。[[IADR-0104]]） |
| `llm.purpose` | `Llm:Routing:PurposeModels` のキー ＋ `default` ＋ `other` | 用途 |
| `llm.model` | route 結果のモデル / `none` | 実際に選択されたモデル |
| `llm.provider` | `claude` / `selfhosted` / `copilot` / `none` | 呼び出し先プロバイダ |
| `llm.confidentiality` | `public` / `internal` / `confidential` / `restricted` | 入力の最高機密区分 |

**カーディナリティ**: 非有界になり得るのは `purpose`（呼び出し側の自由文字列）と `stop_reason`
（未知値を原文透過する。[[IADR-0104]] / [[IADR-0109]]）の 2 つで、いずれも既知集合以外は **`other` へ集約**する。
`model` / `provider` はルーティング設定（`Llm:Routing`）由来、`confidentiality` は `SensitivityClass`（4 値）、
`result` は実装の終了経路の列挙であり、いずれも有限である。**プロンプト・本文・利用者識別子・
エンドポイント URL は属性にしない**。現構成での実系列数は数十のオーダー。

**未知値の原文**はメトリクスには載らない。原文が必要な調査はログ側で行う
（`CompletionEndpoints.LogStopReason` の warn ／ [[IADR-0109]] の未写像 `finish_reason` の warn）。
メトリクスは「傾向を見る面」、ログは「個別を追う面」と役割を分ける。

## クエリ例（PromQL）

```promql
# 拒否率（直近 30 分）: 送信が成立した呼び出しのうちモデルが拒否した割合
sum(rate(llm_completion_total{llm_stop_reason="refusal"}[30m]))
  / clamp_min(sum(rate(llm_completion_total{llm_result="sent"}[30m])), 1e-9)

# 用途別の拒否率（trade-decision の恒常的拒否を検知する。AST では拒否＝全判断 Hold へ縮退）
sum by (llm_purpose) (rate(llm_completion_total{llm_stop_reason="refusal"}[30m]))
  / clamp_min(sum by (llm_purpose) (rate(llm_completion_total{llm_result="sent"}[30m])), 1e-9)

# 上限到達率（IADR-0101 の max_tokens 見積もりの妥当性 / #380 の実測材料）
sum(rate(llm_completion_total{llm_stop_reason="max_tokens"}[30m]))
  / clamp_min(sum(rate(llm_completion_total{llm_result="sent"}[30m])), 1e-9)

# 越境拒否（機密区分により送信しなかった）件数 — 拒否（refusal）とは別軸
sum by (llm_confidentiality) (rate(llm_completion_total{llm_result="egress_denied"}[30m]))

# 呼び出し先障害 — 拒否・越境拒否と切り分ける
sum by (llm_provider) (rate(llm_completion_total{llm_result="upstream_error"}[30m]))

# 未定義 purpose の流入（ルーティングが既定へ落ちている状態の遅い警報。IADR-0102 / IADR-0106 の罠）
sum(rate(llm_completion_total{llm_purpose="other"}[1h]))

# フォールバックの発火（用途別・モデル別）。llm_model は「見送った候補」である（ADR-0038 決定 6 / #863）
sum by (llm_purpose, llm_model) (rate(llm_completion_total{llm_result="fallback"}[30m]))

# フォールバック率 = 見送った呼び出し ÷ 成立した呼び出し。恒常的に高いなら第 1 候補の割当を疑う
sum(rate(llm_completion_total{llm_result="fallback"}[30m]))
  / clamp_min(sum(rate(llm_completion_total{llm_result="sent"}[30m])), 1e-9)
```

> **注意**: 分母 0 で `NaN` にならないよう `clamp_min` を用いる。Prometheus 側の属性名はドットが
> アンダースコアへ変換される（`llm.stop_reason` → `llm_stop_reason`）。

### ★ 系列が在っても `rate()` は 0 を返しうる（実測 2026-08-16・#786）

**上のクエリは「トラフィックが流れていること」を前提にしている。** 稼働中の k3s で実測した結果:

| 状況 | 結果 |
| --- | --- |
| `/complete` を 6 回呼んだ直後 | `count(llm_completion_total)` = **6**（系列は出る） |
| 同時点の `sum(rate(...{llm_stop_reason="max_tokens"}[30m]))` | **0** |
| `refusal` のクエリ | **空ベクタ** |
| 上限到達率 | **NaN → 0** |

理由は 2 つある。

1. **6 回の呼び出しがすべて別のラベル組み合わせになり、各系列がサンプル 1 点しか持たなかった。**
   `rate()` は同一系列に 2 点以上を要求する。
2. **`refusal` は一度も起きていないため系列自体が存在しない。** Prometheus は
   「起きていないラベル値」を 0 として持たない（`clamp_min` は分母 0 を救うが、**空ベクタは救わない**）。

> **#380 の材料としてこれらのクエリを使うときは、「計器が在る」だけでは足りない。**
> **同一ラベル組み合わせで反復するトラフィック**が保持期間のあいだ蓄積している必要がある。
> 保持期間そのものは #787 / [[IADR-0210]] で永続化した。

## 出力トークンの分布（`llm_completion_output_tokens`・[[IADR-0212]] / #786）

`max_tokens` の妥当性（[[IADR-0101]] の 4096）は**回数ではなく分布**でしか読めない。
バケット境界は **4096 付近を細かく刻んである**（`… 1024, 2048, 3072, 4096, 8192`）。

**属性は Counter の 6 つから `llm.result` を落とした 5 つ**である
（Histogram は**送信が成立した経路だけ**に記録するため、`result` は常に `sent` で系列を分けない）。

```promql
# 出力トークンの p95（用途別）。4096 に張り付いていれば max_tokens が足りていない
histogram_quantile(0.95,
  sum by (le, llm_purpose) (rate(llm_completion_output_tokens_bucket[1h])))

# 上限のすぐ下に山があるか（3072 超の割合）。IADR-0101 の再調整の直接の材料
1 - (
  sum(rate(llm_completion_output_tokens_bucket{le="3072"}[1h]))
    / clamp_min(sum(rate(llm_completion_output_tokens_count[1h])), 1e-9))

# 平均出力トークン（モデル別）— 単価表の見積もりと突き合わせる（IADR-0164）
sum by (llm_model) (rate(llm_completion_output_tokens_sum[1h]))
  / clamp_min(sum by (llm_model) (rate(llm_completion_output_tokens_count[1h])), 1e-9)
```

> **`llm_completion_output_tokens_count` は `llm_completion_total{llm_result="sent"}` と一致しない。**
> Counter は**未送信も計上する**（拒否率の分母が欠けないように。[[IADR-0110]]）が、
> Histogram は**送信が成立した経路だけ**を数える（[[IADR-0212]] 決定 3）。
> **この非対称は意図である** —— 送信していない呼び出しに出力トークン数は存在せず、
> 0 で埋めると分布の最下段が「短い応答」と「応答が無かった」の混合になる。

## フォールバックの発火（`llm.result="fallback"`・[[IADR-0225]] / #863）

計画 [`ADR-0038`](../../planning/projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md)
決定 6 が求める「フォールバック発火の可観測化」は、**新しい計器ではなく `llm.result` の 5 番目の値**で満たす。

| 値 | 意味 | `llm.model` |
| --- | --- | --- |
| `fallback` | 上流が **HTTP 400 系**を返し、**次の候補モデルへ切り替えた**呼び出し | **見送った**候補 |
| `sent` | 越境が成立した呼び出し | **実際に使った**候補 |

**フォールバックが起きた 1 リクエストは 2 件計上される。** これは意図である ——
上流を実際に 2 回叩いており、**用途別・モデル別の利用実績としては 2 回として読むのが正しい**。

- **`llm_completion_total` の総和はリクエスト数より大きくなり得る。** 一方
  **拒否率の分母（`llm_result="sent"`）はリクエストあたり最大 1 件**であり、上の拒否率の式は影響を受けない。
- **`upstream_error` には混ぜていない。** 混ぜると「フォールバックで回復した呼び出し」が
  呼び出し先障害の率へ入り、下表の `upstream_error` 率 > 10%（critical）が誤発火する。
- **429（レート制限）ではフォールバックしない**（`ADR-0038` 決定 4）。429 は再試行の対象であり、
  現行実装では従来どおり `upstream_error` として計上される（429 の再試行そのものは
  [[IADR-0225]] §フォローアップ 1 として未実装である）。
- **既存の Grafana パネル**（`sum by (llm_result) (increase(llm_completion_total[$__range]))`）に
  新しい系列としてそのまま現れるため、**ダッシュボードの変更を要さない**。

## しきい値の方針（アラート）

| 観点 | 目安 | 重大度 | 意図 |
| --- | --- | --- | --- |
| 全体の拒否率 | > 5% が 30 分継続 | warning | 既定モデル（Opus 5・ADR-0025）の安全性分類器による劣化を検知する |
| `purpose` 別の拒否率 | > 20% が 30 分継続 | warning | 特定用途のプロンプトが恒常的に拒否されている（AST は Hold へ縮退） |
| `upstream_error` 率 | > 10% が 10 分継続 | critical | 呼び出し先障害。拒否とは対処が異なる |
| `llm.purpose="other"` の出現 | > 0 が 1 時間継続 | warning | 未定義 purpose＝ルーティングが既定へ無音で落ちている疑い |

数値は**初期値であり実測前の出発点**である。運用開始後の実測で調整する。アラートルールの実配線
（`deploy/prometheus/alerts.yml` への追加と Alertmanager 通知）は [[IADR-0110]] §フォローアップ 1 として
別作業に切り出す（稼働中環境への操作を伴うため）。

## 関連仕様

- 実装 ADR: `../adr/IADR-0110_llm-completion-stop-reason-metrics.md`（本メトリクスの決定）、
  `../adr/IADR-0104_llm-stop-reason-refusal.md`（`Sent` と `StopReason` の軸の分離）、
  `../adr/IADR-0109_openai-finish-reason-normalization.md`（正準語彙への正規化。**#394 / PR #415 で追加**。
  本 PR が先に develop へ入る場合、当該ファイルが揃うまでこのリンクは一時的に未解決になる）
- 機能仕様書: `../functional/FR-11_llm-egress-routing.md`
- テスト仕様書: `../tests/FR-11_llm-egress-routing.md`（T-21）
- 運用仕様書: `../operations/operations.md`（監視・アラート）
- 作業仕様書: `../specs/20260728_issue-395_refusal-metrics.md`

## 未決事項

- アラートの実配線（Prometheus ルール／Alertmanager 通知先）と Grafana ダッシュボードへのパネル追加。
- レイテンシ・トークン消費のヒストグラム（[[IADR-0110]] §フォローアップ 2。#380 のコスト実測と接続する）。
- **フォールバック率のしきい値**（[[IADR-0225]] §フォローアップ 3）。**実測前に数値を置かない。**
- 埋め込み経路（`/embeddings`）の同種メトリクス。
