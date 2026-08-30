---
title: LLM 利用実績（トークン消費量・金額換算・単価表）ログ・可観測性仕様書
type: observability-spec
status: in-progress
author: claude
created: 2026-08-23
updated: 2026-08-30
---
<!-- trace:
ids: [FR-10, FR-11, NFR, UC-05, SC-10]
adrs: [ADR-0006, ADR-0010, ADR-0022, ADR-0025, ADR-0038, ADR-0044]
iadrs: [IADR-0110, IADR-0164, IADR-0212, IADR-0225, IADR-0265, IADR-0304, IADR-0312]
specs: [20260823_issue-443_llm-usage-metrics-and-pricing]
issues: [#380, #443, #546]
-->

# 可観測性仕様書: LLM 利用実績（トークン消費量と金額換算）

## 起点

- 計画は **用途別・モデル別**の計測と、**有効期間つき単価表**、**金額換算は単価表を読む側（LLM ゲートウェイ）
  で行う**ことを確定させている。**Grafana のクエリやレコーディングルールに単価を書かない** ——
  式に単価が散ると**有効期間を判定する主体が存在せず、期限切れの警告を出せる場所が無くなる**ためである。
- 呼び出し回数・終了理由は [`llm-completion-metrics.md`](llm-completion-metrics.md) が定める（別の面である）。

## メトリクス定義

| 項目 | 値 |
| --- | --- |
| Meter 名 | `microservices-platform.llm-gateway`（呼び出し回数と同じ Meter） |
| 発行元 | `LlmGateway` の `/complete`・`/complete/stream` の**送信が成立した経路だけ** |
| 収集経路 | OTel SDK → OTLP（`Otlp:Endpoint`）→ Collector → Prometheus |

| 計器 | 種別 | 単位 | Prometheus 側の名前 | 用途 |
| --- | --- | --- | --- | --- |
| `llm.tokens.total` | Counter | `{token}` | `llm_tokens_total` | トークン消費量の累計（費用の分子） |
| `llm.cost.total` | Counter | `{currency}` | `llm_cost_total` | 金額換算の累計 |
| `llm.pricing.unpriced.total` | Counter | `{completion}` | `llm_pricing_unpriced_total` | **単価を解決できなかった呼び出し**（0 が正常） |

### 属性（すべて有限集合）

| 属性 | 値域 | 付く計器 |
| --- | --- | --- |
| `llm.purpose` | 用途設定のキー ＋ `default` ＋ `other` | tokens / cost |
| `llm.model` | ルータが選んだモデル / `none` | tokens / cost / unpriced |
| `llm.provider` | `claude` / `selfhosted` / `copilot` / `none` | tokens / cost |
| `llm.confidentiality` | `public` / `internal` / `confidential` / `restricted` | tokens / cost |
| `llm.token_type` | `input` / `output` | tokens |
| `llm.currency` | 単価表の通貨（既定 `USD`） | cost |
| `llm.pricing_status` | `out_of_period` / `no_entry` | unpriced |

**利用者識別子・プロンプト・本文は属性にしない**（カーディナリティが非有界であり、個人の利用行動の
記録に踏み込む）。値域の正規化は呼び出し回数のカウンタと**共有**しており、両者は同じ軸で読める ——
用途別モデル振り分けの効果は、振り分けの前後を同じ軸で比較して初めて測れるためである。

## 単価表

`Llm:Pricing` 節に、モデルごとの**有効期間つき**の単価（百万トークンあたりの入力・出力）を持つ。

- **区間は半開区間 `[EffectiveFrom, EffectiveTo)`。** 開始は含み、終了は含まない。
  「終了日 = 次の開始日」で書ける形にしつつ、切替時刻ちょうどに 2 区間が該当することも、
  1 秒の隙間が空くことも起こさない。省略は無限（過去方向 / 未来方向）を意味する。
- **区間の重なり・空区間・負値は起動時に落とす。** 重なりを実行時に先勝ちで解決すると、
  どちらの単価で換算したかを後から特定できない。
- **単価改定は設定変更だけで反映できる。** 反映そのものは人手に残る（下記の警報が漏れを検知する）。

## 🔴 単価を解決できなかったときの扱い

**無音で 0 円として扱わない。** 該当する単価が無い呼び出しでは次の 3 つを同時に行う。

1. 警告ログ（モデル名と、期限切れ／未登録の別）
2. `llm.pricing.unpriced.total` を計上（`llm.pricing_status` で理由を分ける）
3. **金額メトリクスへは 1 件も積まない**

**3 が要点である。** 0 を積むと、**期限切れが「費用の減少」に化けて**費用増加の検知をすり抜ける。
**金額が過小であることは金額そのものからは読めない** —— 読めるのは 2 のカウンタだけである。

## 記録しない経路

| 経路 | 理由 |
| --- | --- |
| 越境拒否（送信していない） | トークンが存在しない。0 を積むと「安く済んだ」と読める |
| プロバイダ未登録・上流エラー | 同上 |
| ストリームが最終チャンクを受け取れずに終わった | 実数が無い。0 埋めをしない |

## クエリ例（PromQL）

```promql
# 🔴 最初に見る: 単価を解決できなかった呼び出し（0 でなければ下の金額は過小）
sum by (llm_pricing_status, llm_model) (increase(llm_pricing_unpriced_total[$__range]))

# 用途別の費用（選択期間の累計）
sum by (llm_purpose) (increase(llm_cost_total[$__range]))

# モデル別のトークン消費量（入出力別）
sum by (llm_model, llm_token_type) (increase(llm_tokens_total[$__range]))
```

**単価は式に現れない。** 金額はゲートウェイが換算済みの値として出す。

## ダッシュボード

`llm-usage`（[`llm-usage.json`](../../deploy/grafana/provisioning/dashboards/llm-usage.json)。
k8s 経路は `deploy/local/observability/grafana.yaml` の ConfigMap に同内容で置く。**片方だけ直すと
経路間パリティの検査が落ちる**）。最上段に「単価を解決できなかった呼び出し」を置き、
**0 でなければ以下の金額が過小であること**を画面上で明示する。

月次の読み方は [`../operations/llm-cost-monthly-review-runbook.md`](../operations/llm-cost-monthly-review-runbook.md) が定める。

## 監視観点

| 観点 | 見るもの | 意味 |
| --- | --- | --- |
| 単価表の陳腐化 | `llm_pricing_unpriced_total` > 0 | **金額が過小である。** 単価表を直すまで金額を信用しない |
| 用途別の費用増 | 費用の前月比 | 1.0 を大きく超える用途 |
| モデル構成の変化 | モデル別の費用 | 上位モデルの比率が上がると回数が同じでも費用は増える |
| 用途の定義漏れ | `llm_purpose="other"` の増加 | 定義していない用途が来ている（ルーティングが既定へ落ちている） |

## 本仕様書が扱わないこと

- **出力トークンの分布**（`llm.completion.output_tokens`）。既定 `max_tokens` の妥当性を上限付近の
  バケットの厚みで読む**別の面**であり、[`llm-completion-metrics.md`](llm-completion-metrics.md) と
  稼働環境での実測の issue が扱う。**本書の累計カウンタとは役割が違い、二重実装ではない。**
- **月次予算の上限アラート**。**［2026-08-30 更新 / #546］通知基盤（Alertmanager）は配備済みになった**が、
  **しきい値が計画側で未確定である**ため置かない（実測を待って確定する。確定の前提は費用の実績が数か月分
  そろうこと）。🔴 **実装側で数字を決めない** —— 決めるとそれが既成事実として計画へ逆流する。
- **基盤と利用側プロジェクトの費用の合算**。合算するとどちらの予算を超過したのか判別できなくなるため、
  計画が明示的に禁じている。
