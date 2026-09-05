---
title: 合成監視トラフィックの標識と、費用・利用状況からの除外 可観測性仕様書
type: observability-spec
status: in-progress
created: 2026-09-05
updated: 2026-09-05
author: claude
---
<!-- trace:
ids: [NFR-02, NFR-21, FR-10, SC-10]
adrs: [ADR-0006, ADR-0044, ADR-0071, ADR-0072, ADR-0076, ADR-0079]
iadrs: [IADR-0378, IADR-0343, IADR-0354, IADR-0367, IADR-0370, IADR-0299, IADR-0110]
specs: [20260905_issue-1203_analysis-ask-absent-companion]
issues: [#1203, #1202, #1204, #1111, planning#524, planning#538]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる -->

# 可観測性仕様書: 合成トラフィックの標識と除外

## 起点

- 起点 issue: [#1203](https://github.com/endazon/microservices-platform/issues/1203)
- 低頻度の経路（`/analysis/ask` 系）は無風でいられる時間が検知要件（5 分）を超え得るため、
  「鳴らない」と「鳴りようがない」を区別できない。**一定間隔で代表リクエストを打つ観測専用の経路**を置く。
- 🔴 **観測が指標を汚す。** 監視のために打ったリクエストが利用実績・費用・検索傾向へ入ると、
  **それらの指標が「人が使った量」を表さなくなる。** 標識と除外は同時に入れる。

## 標識

**判定材料は面ごとに違う。同じ 1 つの材料で両方は賄えない。**

| 面 | 到達性 | 判定材料 | 偽装できるか |
| --- | --- | --- | --- |
| **外周**（BFF の `/bff/*`、DashboardService の `/dashboard/events`） | 外部から到達し得る | **検証済み JWT の主体**（`azp` / `client_id` / `preferred_username` / `sub` が許可集合に一致） | **できない**。他人の `azp` を名乗るトークンの発行にはクライアント資格情報が要る |
| **内周**（AiAnalysisService → LlmGateway） | ClusterIP ＋ NetworkPolicy 既定拒否 ＋ STRICT mTLS の内側 | ヘッダ `X-Synthetic-Traffic`（**外周が付ける**） | 同一ネットワーク内からのみ（内部 API と同じ受容） |

🔴 **外周は受信ヘッダを一切見ない。** 見れば「外から印を付けて実利用を費用集計から隠す」経路ができる。
この否定形は試験で固定している（外からヘッダを付けても除外されないこと）。

🔴 **fail-closed。** 許可集合（`SyntheticMonitoring:Subjects`）が空なら**何も合成と見なさない**。
逆向き（空＝全部が合成）に倒すと、設定漏れで実利用がまるごと費用計上から消える。

## 除外の入れ場所

| 対象 | 入れ場所 | 理由 |
| --- | --- | --- |
| 利用状況（検索・回答の件数） | BFF の発火の口（`IUsageEventReporter.Report`） | **唯一の発火点**。ここを通さなければ行が入らない |
| 検索傾向（最小出現件数のしきい値） | 同上（独立の除外は置かない） | 行が入らないなら、しきい値を通過する語も生じない |
| 利用イベントの受け口 | DashboardService の `POST /dashboard/events` | 多層防御。**行を作る側の性質**にする（直接叩かれた場合） |
| LLM 費用（トークン累計・金額） | LlmGateway の `/complete` と `/complete/stream` | 単価を解決して金額を積む主体がそこである |

**SLO の分子分母からは外さない。** 合成監視を置く目的が「評価対象を作ること」であり、
そこから外すと器の意味が消える。

## 除外を数える計器

🔴 **黙って落とさない。** 落とした件数がどこにも無いと、
**「合成だけが通っていて実利用は 0」でも計器が緑に見える。**

| 計器 | 種別 | 単位 | Prometheus 側の名前 | 意味 |
| --- | --- | --- | --- | --- |
| `usage.event.dispatch.total{usage.event.outcome="excluded_synthetic"}` | Counter | `{event}` | `usage_event_dispatch_total` | 合成のため利用イベントを発火しなかった件数 |
| `llm.usage.synthetic_excluded.total` | Counter | `{completion}` | `llm_usage_synthetic_excluded_total` | 合成のため費用へ計上しなかった補完の件数 |

**`excluded_synthetic` を `dropped` と混ぜない。** あちらは受け口の不調による取りこぼしで、
こちらは設計どおりの除外である。混ぜると「除外が効いている」と「受け口が壊れている」が同じ数になる。

**属性にトークン数を載せない。** 非有界であり、カーディナリティが爆発する。
除外した量を知りたいときは出力トークンの分布を読む。

### 読み方

```promql
# 合成のため外した利用イベント（種別別）
sum by (usage_event_type) (increase(usage_event_dispatch_total{usage_event_outcome="excluded_synthetic"}[$__range]))

# 合成のため費用へ入れなかった補完（用途別）
sum by (llm_purpose) (increase(llm_usage_synthetic_excluded_total[$__range]))
```

🔴 **除外が伸びていて実利用が伸びていないときは、実利用が 0 である。**
除外は指標を守るためのものであり、**費用そのものは減らさない。**

## 費用（現状の配備では 0）

合成の要求は AiAnalysisService が LLM を呼ぶ手前で縮退させる（`SyntheticMonitoring:AllowLlmEgress`、既定 `false`）。
したがって**現状の配備で**恒常的に発生する費用は無い。

🔵 **［2026-09-05 更新］「未確定だから空けてある」ではなくなった。**
従前ここには「実行頻度と費用上限が計画側で未確定であるため、そこは意図的に空けてある（裁定待ち）」と
書いていた。**計画は裁定を下し、実行間隔を 2 段に分けた。**

| 用途 | 間隔 | LLM | 配備 |
| --- | --- | --- | --- |
| 常時トラフィックの生成 | **60 秒** | **呼ばない**（検索までは走る） | ✅ 済（opt-in オーバーレイ） |
| SLO 評価用 | **60 分** | **呼ぶ** | 🔴 **未着手** |

**費用の上限は絶対額では置かない。間隔が実質的に上限を固定する**（60 分＝月 720 回・概算 月約 4,400 円）。

🔴 **その代わり、初回トークンの計器は合成では記録されない** ——
最初のトークンが 1 件も出なければ記録しない設計だからである。
**初回応答の SLO について評価対象を常時存在させるには、合成が実際に LLM を呼ぶ必要がある。**
**60 分側が未着手である以上、初回応答の SLO の評価対象はまだ生まれない。** これは裁定待ちではなく実装の残作業である。

## `absent` の併設との関係

**常時トラフィックを作ることが合成監視の目的であり、その到達点が「評価対象の不在」を鳴らせる状態である。**
60 秒側が着地したことで、**一括経路 `/analysis/ask` の HTTP 系列に対する不在検知が入った**
（アラート定義は `deploy/prometheus/alerts.yml`。`docs/` の外なのでリンクは張らない）。

🔴 **初回トークンの系列には入れていない。** LLM を呼ばない限り系列が立たず、
いま `absent` を置くと**恒常発火**になる —— それは計画が却下した案そのものである。
**60 分側が配備されてから、周期 60 分の 2 周期ぶんの窓（`absent_over_time(…[2h])`）で入れる。**
