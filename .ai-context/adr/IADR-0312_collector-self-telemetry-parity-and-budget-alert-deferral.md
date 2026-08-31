---
title: IADR-0312 collector の自己テレメトリ宣言は検査器で揃え、LLM 予算アラートは分けて置かない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - NFR-21
  - SC-10
  - FR-11
  - ADR-0006
  - ADR-0044
  - IADR-0130
  - IADR-0164
  - IADR-0165
  - IADR-0265
  - IADR-0304
author: claude
created: 2026-08-30
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# IADR-0312: 転送構成の自己テレメトリ宣言と、LLM 予算アラートを置かない判断（#1090 / #546）

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装）

## 起点・関連

- **NFR-21**（障害検出 5 分以内 / MTTR 30 分以内）。計画 ADR: **ADR-0006**（アラートは Alertmanager を用いる）／
  **ADR-0044**（LLM 利用実績の計測粒度と単価表）
- 実装 issue: **#1090**（転送構成の collector 設定にだけ `telemetry.metrics.address` が無い）／
  **#546**（Alertmanager 未配備で LLM 予算超過に気づく手段がゼロ）の残作業
- 先行: [IADR-0304](./IADR-0304_alertmanager-deployment-and-null-receiver.md)（配備と `default-null`。
  **「検査器は足さない」と明記している** —— 本 ADR はその判断を、同型 2 回目の発生をもって改める）／
  [IADR-0164](./IADR-0164_llm-cost-monthly-review-interim-control.md)（月次の手動確認）／
  [IADR-0165](./IADR-0165_grafana-interim-alerting.md)（Grafana 統合アラートへの暫定配線）
- 作業仕様書: [20260830_issue-1090-546](../specs/20260830_issue-1090-546_collector-telemetry-parity-and-alert-delivery.md)

## コンテキストと課題

`IADR-0304` は経路B の collector に `containerPort: 8888` / Service 8888 /
`service.telemetry.metrics.address` を足して scrape 断を塞いだ。**しかし塞いだのは fail-safe 側だけだった。**

経路B には collector 設定が 2 つある（同名 ConfigMap を上書きし合う排他関係）。

| 設定 | 役割 | `telemetry.metrics.address`（是正前） |
| --- | --- | --- |
| `deploy/otel-collector-config.yaml` | compose | あり |
| `deploy/local/infra/otel-collector.yaml` | 経路B **既定**（debug のみ・fail-safe） | あり |
| `deploy/local/observability/otel-collector-forward.yaml` | 経路B **転送**（`OBSERVABILITY=1` の opt-in） | **無い** |

**欠けていたのは「実際に何かを測ろうとしたときにだけ効く」側である。** Prometheus の scrape 対象は
`otel-collector:8888` **ただ 1 つ**であり、転送を有効にした瞬間に唯一の対象が落ちうる。

🔴 **稼働中の collector 自身がこの危険を出力している**（実測 2026-08-30）:

```
warn localhostgate/featuregate.go:63  The default endpoints for all servers in components
     will change to use localhost instead of 0.0.0.0 in a future version.
```

**版を上げたときに壊れる。そのとき原因は「版を上げたこと」ではなく「片方にしか宣言が無いこと」になる。**

## 決定

### 決定 1 — 単一情報源にはできない。**乖離を消すのではなく止める**

3 つは**排他的な別配備の全体設定**であり、共有できる形が無い。

- **kustomize の strategic merge patch は届かない。** ConfigMap の `data.config.yaml` は
  **不透明な文字列 1 個**であり、YAML の内側へ patch を当てられない。
- **`configMapGenerator` で共有ファイル化もできない。** kustomize は root 外を参照できず、
  compose 用の設定を overlay から読めない（`prometheus.yaml` / `grafana.yaml` が
  **既に同じ理由で inline 二重管理を選んでいる**）。
- **collector の `--config` 多重指定でマージする**案は、**1 行のために**起動引数・追加 ConfigMap・
  マウントを増やす。dev 限定の設定に対して過剰であり、「過剰な抽象化を行わない」に反する。

したがって**二重（三重）管理を受容し、乖離を機械で止める**。これは経路B が既に採っている方針と同じである。

### 決定 2 — 検査器 `check-collector-self-telemetry.js` を新設する（**同型 2 回目**）

`CLAUDE.md` は「検査器・規約の追加は**同型の事故が 2 回起きたら**を条件とする（1 回目は記録に留める）」と定める。

| 回 | 事故 | 記録 |
| --- | --- | --- |
| 1 | 経路B の collector が `containerPort` / Service / `address` を持たず `up` が恒常 0 | `IADR-0304`（**検査器は足さないと明記**） |
| 2 | **転送構成にだけ** `address` が無い（#1090） | 本 ADR |

**条件を満たしたので足す。** 見るのは 4 点である。

1. すべての collector 設定が `service.telemetry.metrics.address` を持つ
2. その待受が**全設定で同一**である（片方だけ変更されるのを止める）
3. 待受のポートが **Prometheus の scrape 対象ポートと一致する**
   （「宣言はあるが番号が食い違う」という**次の形**を先に塞ぐ）
4. 待受が loopback でない（宣言はあるがコンテナ外から到達できない形）

**列挙を持たない。** `receivers.otlp` ＋ `service.pipelines` ＋ `exporters` を持つ YAML を
`deploy/` の走査で発見する —— ファイル名で絞ると、既に 3 通りある命名の 4 通り目を落とす。
走査 0 件なら fail する（`IADR-0130`）。

🔴 **`check-deploy-manifests.js`（kubeconform）では捕まらない。** `service.telemetry` は
**任意項目でありスキーマ上は無くても正当**である。**「正当だが壊れる」を見るのが本検査器の役目である。**

### 決定 3 — **LLM コストの予算アラートは置かない。** 別 issue（#1111）へ分ける

#546 の主目的は「予算超過に気づく手段」である。素直に読めば `llm_cost_total` にしきい値を置くのが答えに見える。
**採らない。根拠は 4 本ある。**

**(1) 計画（`fixed`・利用者裁定 2026-08-08）が明示的に禁じている。**

> **月次予算の金額（しきい値）は定めない。実測を待って確定する。** 稼働実績が無い段階で置いた絶対額は、
> 超過しても過少でも判断の根拠にならない。

同書 §リスク・未決事項は、確定の前提を「**費用の実績が数か月分そろうこと**」と定める。
**実装が数字を決めると、それが既成事実として計画へ逆流する**（計画側が 2026-08-29 に
陳腐化しきい値で同じ危険を名指ししている）。

**(2) 系列が無い。しかも「まだ呼ばれていない」のではなく、稼働イメージに計器が無い**（実測 2026-08-30）。

```
$ kubectl -n microservices-platform exec llmgateway-service-... -- \
    sh -c 'tr -d "\000" < /app/LlmGateway.Api.dll > /tmp/s.txt; grep -c ...'
llm.completion.total: PRESENT
llm.tokens.total:     ABSENT
llm.cost.total:       ABSENT
LlmUsageMetrics:      ABSENT
```

**リポジトリのソースには実装済みである**（`LlmUsageMetrics.cs`。2026-08-23 / #443）。
**稼働イメージが古い。** どちらにせよ、**いま置いたアラートは評価対象を 1 本も持たない。**

> 🔴 **［2026-08-31 追記 / #1090］この脚は弱まった。再測して自分で否定しておく。**
> develop を取り込む間にイメージが再ビルドされ、**稼働イメージは計器を持つようになった**
> （アセンブリの置き場も `/app/LlmGateway.Api.dll` → `/app/LlmGateway.dll` へ変わっている）。
>
> ```
> $ POD=llmgateway-service-796cf8f4dd-4m4vl
> $ kubectl -n microservices-platform exec $POD -c llmgateway-service -- sh -c 'set -e
>     tr -d "\000" < /app/LlmGateway.dll > /tmp/s.txt
>     wc -c < /tmp/s.txt          # 101763 ← 陽性対照（走査が空でないこと）
>     for s in llm.completion.total llm.tokens.total llm.cost.total LlmUsageMetrics LlmGateway; do ...'
>       wc -c < /tmp/s.txt          # 101763 ← 陽性対照（走査が空でないこと）
>       for s in llm.completion.total llm.tokens.total llm.cost.total LlmUsageMetrics LlmGateway; do ...'
> llm.completion.total: PRESENT
> llm.tokens.total:     PRESENT
> llm.cost.total:       PRESENT
> LlmUsageMetrics:      PRESENT
> LlmGateway:           PRESENT   ← 陽性対照
> ```
>
> **それでも `llm_*` の系列は 0 件のままである**（実測 2026-08-31）。理由が
> 「**計器が無い**」から「**まだ 1 度も呼ばれていない**」へ変わっただけで、
> **評価対象が無いことは変わらない。**
>
> 🔴 **初回の測定は危うく誤答するところだった。** 旧パスのまま測ると `sh` の
> `cannot open /app/LlmGateway.Api.dll` を見落として**全部 ABSENT** と読める。
> **陰性の結論には陽性対照を対で置く**（`wc -c` と `LlmGateway`）。
>
> **決定 3 は変えない。** 脚 (1)(3)(4) は無傷であり、とくに **(1) 計画が禁じている**は単独で決定的である。

**(3) 経路B では月次スケールの規則が原理的に評価できない。** 保持は
`--storage.tsdb.retention.time=7d` であり、**稼働クラスタは PVC を持たない**（Pod 再起動で消える）。
🔴 **PVC が「無い」のではなく「あるのに繋がっていない」**（再測 2026-08-31）——
`prometheus-data`（5Gi）は `Bound` だが、Deployment の `volumes` は ConfigMap 1 つだけである。
**存在の確認で満足すると見落とす。マウントを見る。**
`[30d]` の窓を持つ規則は dev で常に空になる。**「置いたが動かない」は「置いていない」より悪い**
（置いてあることが統制の存在に読める）。

**(4) 計画 決定 39 が「併存させない」と定めている。** 月次の手動確認（`IADR-0164`）が生きている間に
部分的な自動統制を足すと、**どちらが正かを決める根拠が無くなる**（2026-08-05 の裁定 Q26 が避けた形と同じ）。

**`absent()` で系列の不在を検知する案も採らない。** いま置けば**恒常的に発火し続ける**。
`IADR-0304` 決定 3 が防ごうとしている「既知の誤報」を、配備の翌日に自分で作ることになる。

### 決定 4 — **月次の手動確認（`IADR-0164`）は終了しない。** 終了条件を読み違えない

計画 決定 39 は「**Alertmanager 配備後は暫定措置を終了し上限アラートへ戻す**」と定める。
**配備は満たされた。しかし「上限アラートへ戻す」が満たされていない**（決定 3）。

🔴 **1 つ目だけを見て終了させると、費用の統制がゼロになる** —— それは #546 が生まれた原因そのものである。
Runbook の終了条件は最初から「配備 **かつ** 配線」の連言で書かれており、**そのとおりに読む。**

### 決定 5 — 「Alertmanager 未配備」と書いた記述を**全数引き直して是正する**

`traceability.repo.md` 規則 10（**是正のたびに、この変更で新たに誤りになる自分の記述を引き直す**）。
配備（#1068）で 9 箇所が誤りになっていた（運用仕様書 5・Runbook 2・観測仕様書 1・機能仕様書 1・
ダッシュボードのテキストパネル 2＝パリティ 2 部）。

**「自動検知が無い」という結論は変えていない。変えたのは理由である**
（通知基盤の不在 → しきい値の未確定）。**理由が古いままだと、配備を見た者が
「基盤ができたのだから統制は働いている」と読む** —— 計画 決定 40 が名指しした型そのものである。

## 実測（2026-08-30・稼働中の Rancher Desktop k3s `v1.35.4+k3s1`）

| 確かめたこと | 結果 |
| --- | --- |
| 転送構成 apply ＋ `rollout restart` 後の collector ログ | `Serving metrics {"address": "0.0.0.0:8888"}` |
| `up{job="otel-collector"}`（転送構成のまま） | **1** |
| アプリのメトリクスが Prometheus へ届くこと | `http_server_request_duration_seconds_count` が 77 系列 |
| `llm.*` 系列 | **0 件**（稼働イメージに計器が無い。上記） |
| **意図的に閾値を割ったときのアラート到達** | **`/api/v2/alerts` に `OtelCollectorDown` が現れた**（下の §結果） |

🔴 **副産物として、SLO ルール 5 件のうち 4 件が評価対象を持たないことが分かった。**
規則は `http_server_duration_milliseconds_count` ＋ ラベル `service_name` / `http_status_code` を見ているが、
実際に届いているのは `http_server_request_duration_seconds_count` ＋ ラベル `job` /
`http_response_status_code` である（単位も ms → 秒）。**別 issue（#1110）へ分けた**（資源も原因も別であり、
本 PR で直すと #1090 の変更が埋もれる）。

## 結果

- **良い影響**: 転送を有効にしても唯一の scrape 対象が生き残る。**同じ欠落は次から CI が止める。**
  「アラートが Alertmanager まで届く」ことが、宣言ではなく**実測**になった。
- **悪い影響 / トレードオフ**:
  - **三重管理は残る**（決定 1）。検査器は乖離を止めるが、**書く手間は減らない**。
  - **費用の自動検知は無いまま**である。埋まったのは前提条件（通知基盤）だけで、
    **気づくまでの時間は依然として月次確認の間隔に等しい。**
  - **実測で分かった 4 件の死んだルールは、この PR では直していない。**
- **フォローアップ**:
  1. **月次予算の上限アラート**（#1111）—— しきい値の確定（計画側）・費用の計器を含むイメージの配備・
     月次スケールを評価できる保持、の 3 つが前提。**分けた issue には、この 3 つを着手前に自分で実測することを
     受け入れ基準として書いた**（番号を移すだけにしない）。
  2. **SLO ルールのメトリクス名・ラベル・単位の是正**（#1110）。**ダッシュボードも同じ名前を使っている**ので同時に直す。
  3. **受信先（メール / チャット）の設定** —— 利用者の判断。満たされた時点で Grafana の暫定経路を閉じる
     （`IADR-0165` 決定 5 / `IADR-0304` フォローアップ 1）。
