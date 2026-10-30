---
title: IADR-0654 SLO 評価用（60 分・LLM を呼ぶ）の合成は別の主体で名乗り、外周が標識の値 slo で運んで内周が要求ごとに LLM を許す。初回トークンの計器は LLM のトークンでだけ進める
type: impl-adr
status: Accepted
related_ids: [NFR-02, NFR-21, FR-04, UC-01, ADR-0079, ADR-0076, ADR-0044, IADR-0378, IADR-0354, IADR-0469]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2335_llm-slo-synthetic-probe.md
---

# IADR-0654: 合成の 2 段の要求ごとの区別（#2335）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2335。親 #2130 の子 B）。2 段の間隔と LLM を呼ぶことは計画 ADR-0079 決定 1・2 が決めている。

## 起点・関連

- 起点 issue: #2335。
- 計画:
  - ADR-0079 決定 1（常時 60 秒・LLM を呼ばない／SLO 評価 60 分・LLM を呼ぶ）
  - ADR-0079 決定 2（LLM を呼ぶ合成を認め、費用の上限は間隔で固定する）
  - ADR-0076 決定 4（合成は標識を持ち、費用計測と SC-10 から除外する）
- 前提:
  - [IADR-0378](./IADR-0378_synthetic-traffic-marker-and-exclusion.md)（標識は外周で主体から決め、内周はヘッダを引き継ぐ）
  - [IADR-0354](./IADR-0354_rag-first-token-latency-metric.md)（初回トークンの計器）
  - [IADR-0469](./IADR-0469_helm-synthetic-monitor-optin-render-time-fail-closed.md)（チャートの 60 秒側）
- 作業仕様書: [20261030_2335_llm-slo-synthetic-probe](../specs/20261030_2335_llm-slo-synthetic-probe.md)。

## 決定

1. **SLO 評価用の合成は、別の主体で名乗る。**
   - Keycloak のクライアントは `synthetic-monitor-slo`（ロールなし・`client_credentials`）。
   - BFF の構成 `SyntheticMonitoring:LlmSubjects` に置く。ここに在る主体は合成でもある（費用計上・SC-10 から外す）。
   - `LlmSubjects` が空なら、SLO の合成は 1 件も存在しない（fail-closed）。
2. **外周（BFF）が主体から分類し、内周へは標識の値で運ぶ。**
   - SLO 評価は `X-Synthetic-Traffic: slo`、常時は従前の `1` を運ぶ。合成でなければ付けない。
   - 分類（`SyntheticTraffic.Classify`）は検証済み JWT の主体だけを見る。受信ヘッダは見ない（外から `slo` を付けても運ばない）。
   - 内周の合成の判定（値が空でないこと）は変えない。LlmGateway の費用の除外は、どちらの値でも効く。
3. **内周（AiAnalysisService）は、要求ごとに LLM を許す。**
   - 合成の要求のうち、値が `slo` のもの（`SyntheticTraffic.IsSloProbeInternalRequest`）だけ LLM を呼ぶ。
   - サービス全体の `AllowLlmEgress` は互換のために残す。チャートは引き続き、立つと描画を止める（60 秒の合成まで LLM を呼ぶため）。
4. **初回トークンの計器は、LLM が出したトークンでだけ進める。**
   - `AskTokenEvent` に `FromModel`（既定 true）を足す。次の縮退の文は false で流す。
     - 閲覧できる文書が無い（`NoAccessAnswer`）
     - 合成の抑止の文、およびゲートウェイの拒否・不調の理由の文
     - 拒否の注記、空本文の注記
   - 端点は `FromModel` の最初のトークンでだけ記録する。
   - 従前は、60 秒の合成と権限の無い要求が「速い初回トークン」を積んでいた（実測。コードで確かめた）。60 分の標本は 1 時間に 1 つなので、そのままでは p95 が偽の標本（60 倍）に埋もれる。SSE の形（`token` の本文）は変えない。
5. **チャート**: `syntheticMonitor.slo`（既定は無効）で次を描く。
   - 別の Deployment `synthetic-monitor-slo`（probe.js とイメージを共用。間隔 3600 秒、経路は `/bff/analysis/ask/stream` の 1 本、自分の主体と Secret）
   - BFF の `SyntheticMonitoring__LlmSubjects__0`
   - 3 サービスの `SyntheticMonitoring__Subjects__1`
   - Keycloak への egress

   描画で止める条件:
   - 60 秒側（除外の標識）が無効
   - 主体が 60 秒側と同じ
   - 間隔が 3600 秒未満（費用の上限が崩れる）
   - 経路が SSE の 1 本以外（初回トークンの標本を作らない経路で、費用だけが出る）
6. **ローカルの起動器は 60 秒側だけを配備する。** SLO 評価用の client secret の ExternalSecret だけを同じ門（`SYNTHETIC=1`）で当てる。Vault の種は bootstrap が入れる。

## 却下した案

- **サービス全体の `AllowLlmEgress` を立て、60 秒のプローブを LLM を呼ばない経路（一括 `/analysis/ask` だけ）にする**:
  - 一括経路でも LLM は呼ばれる（`CompleteAsync`）。60 秒の合成が月 43,200 回 LLM を呼ぶ。
  - 経路で区別すると、利用者の要求の種類で費用の可否が決まり、主体と結びつかない。
- **内周へ本文（proto・JSON）の項目で運ぶ**: IADR-0400 決定 3 と同じ理由で採らない。全経路の不変契約に残り、呼び出し元が「試験のため」に立てる誤用の口になる。
- **別のヘッダ名を足す**: 標識の定義が 2 つになり、片方だけ直る事故の口になる。値で区別すれば、合成の判定は 1 つのまま保てる。
- **SLO 評価のプローブを CronJob にする**: 60 秒側と同じ理由（メッシュ内の Job はサイドカーで完了しない）で、Deployment のループにした。間隔は env で与え、3600 秒未満は描画で止める。
- **初回トークンの計器に合成の属性を足して、PromQL で除く**: 属性の値域が広がる。60 秒の合成の縮退は「LLM の初回トークン」ではないので、そもそも積むべきでない。

## 帰結・残余リスク

- **有効化には、SLO の主体が読める文書が 1 つ以上要る**（ABAC）。無いと縮退して標本が出ない（費用も出ない）。
  - ポリシーはライブ環境の作業なので、配備の手順（運用仕様書）に書いた。
  - 現行の 60 秒の主体は ABAC ポリシーを持たない（文書を読めない主体）ので、この点は 60 秒側と違う。
- **LLM の費用が出る**（月 720 回・概算 月約 4,400 円）。チャートの既定は無効で、有効化は課金の承認の後に行う。費用は `llm_usage_synthetic_excluded_total` に積まれる。
- 初回トークンの系列の不在の警報（`absent_over_time(…[2h])`）は、既定が無効のため入れていない（有効化した配備で入れる）。
- 手元に helm が無いので、チャートの描画の試験（`helm-synthetic-monitor.test.js`）は CI（static-checks-units）で初めて走る。
