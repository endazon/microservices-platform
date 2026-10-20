---
title: 取り込みの総枠（embedding-budget / lexical-budget）の使い切りに警報 IngestionBudgetExhausted（直近 1 時間の前進 > 0・5 分・warning）を張り、Prometheus・Grafana の経路 A/B の 4 か所に揃える（#1991）
type: spec
status: done
related_ids: [FR-02, FR-03, UC-04, NFR-21, ADR-0127, ADR-0027, ADR-0006, IADR-0566, IADR-0560, IADR-0536, IADR-0420]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 1: 高機密は語彙索引だけ）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・デッドレター）
issue: "#1991"
---

# 仕様書: 取り込みの総枠の使い切りに警報を張る（#1991）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。実装の基点は MSP `origin/develop` `b75bfebf`。判断は IADR-0566。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取り込み）・FR-03（検索に現れる）
- ユースケース: UC-04
- 非機能: NFR-21（可観測性・警報）
- 関連 ADR: ADR-0127 決定 1・ADR-0027（デッドレター）・ADR-0006（可観測性）
- 関連 IADR: IADR-0560（語彙索引の総枠）・IADR-0420（`increase(...[1h]) > 0` の流儀）

## 判断の材料（現状の実測）

| 観点 | 現状 | 出典 |
| --- | --- | --- |
| 総枠を使い切った文書 | 残りのチャンクを書かず、再試行せずデッドレターへ。一部の点だけが検索に現れる | `EmbeddingBudgetDeadLetterPolicy.IsBudgetExhausted`（`embedding-budget` / `lexical-budget`） |
| 計器 | `messaging.consumer.timeout{messaging.timeout.target}` が 1 件ごとに前進（`ConsumerCallTimeouts.BudgetExhausted`）。Prometheus 名は `messaging_consumer_timeout_total` | `ConsumerTimeoutMetrics.cs` |
| デッドレターの滞留の警報 | **無い**（`alerts.yml` の follow-up の `RabbitMqDeadLetter` はコメントのまま。デッドレターの計器が無い） | `deploy/prometheus/alerts.yml` 末尾 |
| `messaging_consumer_timeout_total` を見る警報 | **無い**（`deploy/` を走査して 0 件） | — |

issue の案「デッドレターの滞留の既存の警報で足りる」は成り立たない。**張る。**

## 形（既存の警報の流儀に揃える）

「0 が正常で、起きたら 1 件でも鳴らす」器の先例 `UnitDocumentsMissingProjectAttribute`（IADR-0420）・`DepartmentSyncNotCorrecting` と同じ形にする。

- Prometheus: `sum by (messaging_timeout_target) (increase(messaging_consumer_timeout_total{messaging_timeout_target=~"embedding-budget|lexical-budget"}[1h])) > 0`・`for: 5m`・`severity: warning`。
  - 窓 1 時間は、単発の使い切りを取りこぼさず、再起動でカウンタが 0 へ戻っても検出が残るため（先例と同じ）。
  - `absent()` を併設しない。平常時は系列が立たない（先例と同じ）。
  - `sum by` で総枠の種類ごとに 1 本にする（段名・Pod で割らない）。
- Grafana（経路 A の compose・経路 B の k8s の写し）: 同じ式・評価器 `gt 0`・`noDataState: OK`・uid `ingestion-budget-exhausted`（40 文字以下）。
- 群は新設の `ingestion-budget`（取り込みの群が無いため）。
- 呼び出しごとの時間切れ（`content` / `embedding` / `vector-store`）は含めない。再試行で回復し得る（デッドレター直行は総枠だけ）。

閾値は「1 件以上」であり、計画の統制値（数値の目標）を新設しない。

## 母集合（規則 9・10）

- 警報の写し: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・`deploy/local/observability/grafana.yaml`（4 か所。IADR-0559 の先例と同じ）。
- 件数の導出値 `25` を `25 件` / `＝ **25**` / `24 → 25` で走査した。追随先: `slo-alerts.yaml` と `grafana.yaml` の前書き（件数・内訳・履歴）、`check-grafana-alerting.js` の前書き。内訳は計算し直した（2+5+6+2+1+2+1+1+1+1+2+2 = 26）。
- 運用仕様書: §埋め込みプロバイダ（総枠の段落）と監視の表。

## 受け入れ基準

- [x] 4 か所に `IngestionBudgetExhausted` があり、`check-grafana-alerting.js`（26 件）と `check-prometheus-alerts-parity.js` が緑。
- [x] 警報が見る計器名・属性名は C# の定数から導いた名前と一致し、呼び出し先の集合はデッドレターへ直行させる集合と一致する（`scripts.repo.test.js` の #1991）。
- [x] 変異: C# の側で総枠の呼び出し先を増やす／警報の呼び出し先を 1 つ外す／計器名を変える、のいずれでも試験が赤。

## 変異の結果

| 変異 | 結果 |
| --- | --- |
| `alerts.yml` の `lexical-budget` を外す | #1991 の試験が赤（呼び出し先の集合が違う） |
| `IsBudgetExhausted` に `ContentTarget` を足す | #1991 の試験が赤（集合が `['content', 'embedding-budget', 'lexical-budget']`） |
| `TimeoutCounterName` を変える | #1991 の試験が赤（名前の固定） |

## 範囲外

- デッドレターの滞留そのものの警報（RabbitMQ の計器の配線が要る。`alerts.yml` の follow-up のまま）。
