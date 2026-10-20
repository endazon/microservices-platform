---
title: IADR-0567 取り込みの総枠（embedding-budget / lexical-budget）の使い切りに警報 IngestionBudgetExhausted（直近 1 時間の前進 > 0・for 5m・warning）を張る。デッドレターの滞留の警報は無く、一部だけ索引された文書に気付く手段がログと計器だけだったため
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, UC-04, NFR-21, ADR-0127, ADR-0027, ADR-0006, IADR-0560, IADR-0420, IADR-0559]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 1）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・デッドレター）
related_specs:
  - ../specs/20261020_1991_ingestion-budget-alert.md
---

# IADR-0567: 取り込みの総枠の使い切りに警報を張る（#1991）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-20
- 決定者: claude（#1991。PR #1989 の AI レビュー 🟡 を別 issue にしたもの）

## 起点・関連

- 起点 issue: #1991（Refs #1746）。
- 関連する計画書 ID: FR-02・FR-03・UC-04・NFR-21。計画 ADR: ADR-0127 決定 1・ADR-0027・ADR-0006。
- 引き継ぐ実装判断: [IADR-0560](./IADR-0560_lexical-index-write-budget.md)（語彙索引の総枠。§残るもの「警報は無い」を本 IADR で閉じる）・
  [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)（「0 が正常」の器の `increase(...[1h]) > 0` の形）・
  [IADR-0559](./IADR-0559_rerank-aware-search-latency-alert.md)（4 か所の写し）。
- 作業仕様書: [20261020_1991_ingestion-budget-alert](../specs/20261020_1991_ingestion-budget-alert.md)（現状の実測・母集合・変異）。

## コンテキストと課題

取り込みの総枠（埋め込み `embedding-budget`・語彙索引 `lexical-budget`。値は `Ingestion:EmbeddingBudgetSeconds`、既定 300 秒）を使い切った文書は、
残りのチャンクを書かずに再試行せずデッドレターへ送られる。その文書は**一部の点だけが検索に現れる**。
運用者が気付く手段は、計器 `messaging.consumer.timeout` と取り込みのログの Warning だけだった。
デッドレターの滞留の警報も無い（`alerts.yml` の follow-up のまま）。黙って一部だけが索引された文書が残り得る。

## 検討した選択肢

1. **張らない**（既存の警報で足りるとする）。デッドレターの警報が無いので成り立たない。採らない。
2. **デッドレターの滞留に警報を張る**。RabbitMQ の計器の配線が要り、本 issue の範囲を超える。総枠以外の失敗も混ざる。採らない。
3. **総枠の使い切りの計器の前進に警報を張る**。計器は既にあり、使い切りの 1 件ごとに前進する。採る。

## 決定

### 決定 1 — 式・窓・for・severity は「0 が正常」の器の先例に揃える

- `sum by (messaging_timeout_target) (increase(messaging_consumer_timeout_total{messaging_timeout_target=~"embedding-budget|lexical-budget"}[1h])) > 0`・`for: 5m`・`severity: warning`。
- 窓 1 時間・`absent()` を併設しない理由は IADR-0420 と同じ（単発を取りこぼさない・再起動で 0 へ戻っても残る・平常時は系列が無い）。
- 閾値は「1 件以上」であり、計画の統制値（数値の目標）を新設しない。

### 決定 2 — 見る呼び出し先は、デッドレターへ直行させる集合と同じにする

- `EmbeddingBudgetDeadLetterPolicy.IsBudgetExhausted` が引く 2 値（`embedding-budget` / `lexical-budget`）だけを見る。呼び出しごとの時間切れ（`content` / `embedding` / `vector-store`）は再試行で回復し得るので含めない。
- 両者の一致を `scripts.repo.test.js`（#1991）が C# の定数から導いて固定する。総枠を足してデッドレターへ送るようにしたら、警報にも足さないと赤になる。

### 決定 3 — 4 か所に揃える

- `deploy/prometheus/alerts.yml`・経路 B の `deploy/local/observability/prometheus.yaml`・Grafana の `slo-alerts.yaml`・経路 B の `grafana.yaml`。
- 群は新設の `ingestion-budget`。Grafana は評価器 `gt 0`・`noDataState: OK`・uid `ingestion-budget-exhausted`。
- ルール数は 25 → 26（`check-grafana-alerting.js` と写しの前書き）。

## 結果

- 総枠を使い切った文書が 1 件でもあれば、およそ 5〜6 分で warning が鳴る。ラベル `messaging_timeout_target` がどちらの総枠かを示す。
- 対応は運用仕様書 §埋め込みプロバイダ（文書の特定・本文を分ける・総枠を延ばす・`DocumentUpdated` の再発行）。

### 残るもの

- 稼働クラスタで鳴ることは未確認（PoC の確認手順は PR 本文）。
- デッドレターの滞留そのものの警報は引き続き無い（follow-up のまま）。

## 関連

- #1991・#1746・#1989
