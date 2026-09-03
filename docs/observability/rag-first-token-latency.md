---
title: RAG 回答の初回応答（初回トークンまでの時間）ログ・可観測性仕様書
type: observability-spec
status: in-progress
created: 2026-09-03
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [NFR-02, NFR-21, FR-04, UC-01, SC-01]
adrs: [ADR-0006, ADR-0076]
iadrs: [IADR-0037, IADR-0110, IADR-0212, IADR-0345, IADR-0365]
specs: []
issues: [#1110, #1204]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる -->

# 可観測性仕様書: RAG 回答の初回応答（TTFT）

## 起点

- 起点 issue: [#1204](https://github.com/endazon/microservices-platform/issues/1204)
- 非機能要求: **RAG 回答の初回応答 p95 が 5 秒以下**。単位は**秒**である。
- 先行: [#1110](https://github.com/endazon/microservices-platform/issues/1110)
  （アラート 5 件のうち 4 件が、稼働 Prometheus に一度も存在しないメトリクス名を見ていた件の是正）。

**「初回応答」と「応答完了」は別物である。** 是正前は `/analysis/ask` の**応答が完了するまでの所要時間**を
初回応答の代理値として読んでいた。しかも画面が実際に使うのは SSE 経路 `/analysis/ask/stream` である。

**指標の定義は変えていない。変えたのは計器の側である。**
応答完了を指標にすると、**長い回答ほど目標を割り、回答品質を上げると指標が悪化する** ——
逆向きの誘因を持つ指標は壊れている。

## メトリクス定義

| 項目 | 値 |
| --- | --- |
| Meter 名 | `microservices-platform.aianalysis-service`（サービス名と一致） |
| 計器 | `rag.answer.first_token.duration`（`Histogram<double>`・**単位 `s`**） |
| 発行元 | `AiAnalysisService` の `POST /analysis/ask/stream` **のみ** |
| 収集経路 | OTel SDK → OTLP（`Otlp:Endpoint`）→ Collector → Prometheus |
| Prometheus 側の名前 | `rag_answer_first_token_duration_seconds_{bucket,count,sum}` |
| バケット境界（秒） | `0.1 / 0.25 / 0.5 / 1 / 2 / 3 / 5 / 8 / 13 / 21` |

**境界に 5 を置いてある。** 目標値そのものであり、境界に無いと `histogram_quantile` が隣の境界へ内挿して、
しきい値の前後で判定が滑る。

### 起点と終点

| | 定義 |
| --- | --- |
| **起点** | `/analysis/ask/stream` のハンドラ入口。ミドルウェア（相関 ID・認証）通過後である |
| **終点** | **最初の `event: token` フレームを応答本文へ書き、フラッシュし終えた時刻**（バイトがサーバを出た瞬間） |

🔴 **`event: citations` では止めない。** 出典は本文のトークンではなく、LLM 生成が始まる前に確定する。
そこで止めると「生成が始まる前の時刻」を初回応答として記録することになる。

🔴 **`token` が 1 件も出なかったストリームは記録しない**（`error` のみ・途中終端・取り消し）。
0 を積むと「初回トークンが無かった」が「速かった」として分布の最下段へ入り、p95 が**下振れする**
（＝ 目標割れを取りこぼす）。記録は 1 ストリームにつき高々 1 回である。

### 属性（すべて有限集合）

| 属性 | 値域 | 意味 |
| --- | --- | --- |
| `ai.purpose` | `rag-answer` / `other` | 用途。`llm_completion_total{llm_purpose=...}` と同じ軸で読める |

**モデル名は載せない。** 使用モデルは `done` イベントで初めて確定し、**初回トークンの時点では未確定**である。
未確定のものを `none` として載せると「モデル別の初回応答」と誤読される。

**プロンプト・質問文・検索語・利用者識別子は載せない**（基数が無界で、時系列 DB の系列数が爆発する）。
値域は既知集合の照合で閉じ、未知値は `other` へ落とす。

## クエリ例（PromQL）

```promql
# 初回応答 p95（アラートが見ているのと同じ式）
histogram_quantile(0.95, sum by (le) (rate(
  rag_answer_first_token_duration_seconds_bucket{job="microservices-platform.aianalysis-service"}[5m])))

# 目標（5 秒）を割った割合 —— エラーバジェットの読み方
1 - (
  sum(rate(rag_answer_first_token_duration_seconds_bucket{le="5"}[30m]))
  / clamp_min(sum(rate(rag_answer_first_token_duration_seconds_count[30m])), 1e-9)
)

# 初回応答の平均（分布が偏っていないかの当たりを付ける。判定には p95 を使う）
sum(rate(rag_answer_first_token_duration_seconds_sum[30m]))
  / clamp_min(sum(rate(rag_answer_first_token_duration_seconds_count[30m])), 1e-9)

# 応答完了との差（生成にかかった時間の目安。**傾向の観察であって判定ではない**）
histogram_quantile(0.95, sum by (le) (rate(
  http_server_request_duration_seconds_bucket{job="microservices-platform.aianalysis-service",
                                              http_route="/analysis/ask/stream"}[5m])))
```

> **注意**: 属性名のドットはアンダースコアへ変換される（`ai.purpose` → `ai_purpose`）。
> 単位 `s` は Prometheus 名の `_seconds` サフィックスへ写る。

## アラート

| ルール | 見るもの | 位置づけ |
| --- | --- | --- |
| `RagFirstTokenP95High` | `rag_answer_first_token_duration_seconds_bucket` の p95 > 5 秒が 10 分 | 🔴 **初回応答の目標判定はこれで行う** |
| `RagLatencyP95High` | `/analysis/ask`（一括経路）の**応答完了** p95 > 5 秒が 10 分 | **傾向の観察に留める。判定に用いない** |

`RagLatencyP95High` の**式は付け替えていない。** 応答完了 p95 は「代理値として読むが判定には用いない」と
計画側が定めており、**残す前提で書かれている**。加えて、名前を保ったまま中身を替えると、
Alertmanager の履歴・過去の実測記録の中で**同じ名前が黙って別の意味になる。**

ルールは 4 ファイル（Prometheus の実体と経路 B の inline、Grafana provisioning の実体と inline）に
同じものを置く。1 対 1 は `node scripts/check-grafana-alerting.js` が突合する。

## ダッシュボード

`microservices-platform-overview`（Grafana）のパネル
**「RAG 初回応答 P95 / RAG first-token (TTFT) P95 (s)」**。
compose と k8s の 2 か所に同内容で置き、`node scripts/check-grafana-provisioning-parity.js` が突合する。

## 🔴 この計器で分からないこと（先に書く）

1. **呼ばれない限り系列が無い。** パネルが空なのは「速い」ではなく「**まだ誰も質問していない**」である。
   無風でいられる時間が検知要件（5 分）を超えるため、**系列の不在を warning とする規則の対象にはできない**
   （恒常発火が警報を無効化する）。区別を付けるには一定間隔で代表リクエストを打つ観測専用の経路が要る。
   **それはまだ配備されていない。**
2. **権限縮退の中立文言も `token` として計上される。** 閲覧できる文書が無いときに出る
   「閲覧権限のある文書が見つかりませんでした。」は LLM を経ずに即座に出るため、**速い値として分布へ入る。**
   利用者から見た「最初の文字が出た時刻」としては正しいが、**LLM 経路だけの分布ではない。**
   縮退と正常を属性で分けるにはオーケストレータ側の信号が要る（イベントは出自を持たない）。
3. **ミドルウェア（相関 ID・認証）の所要時間を含まない。** 起点はハンドラ入口である。
   その差分は `http_server_request_duration_seconds` との比較で観測できる。
4. **BFF から利用者ブラウザまでの区間を含まない。** 測っているのはサービスの応答であり、
   ネットワークと画面描画は入らない。
