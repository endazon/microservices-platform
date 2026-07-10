---
title: 性能（NFR）負荷試験 テスト仕様書
type: test-spec
status: in-progress
related_ids:
  - NFR
  - FR-02
  - FR-03
  - IADR-0052
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 性能・受け入れ基準)"
---

# テスト仕様書: 性能（NFR）負荷試験（#196）

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR）: 性能目標（検索 p95 ≤ 1.5s／RAG 初回 p95 ≤ 5s／取り込み ≥ 1 万件・時／更新 15 分以内反映）と
  受け入れ基準「主要画面・API で p95 レイテンシ目標を満たす（負荷試験で確認）」
- 関連 FR: FR-03（横断検索）／FR-02（取り込み）
- Issue: #196

## テスト対象・範囲

- 対象: `/bff/search`（検索 p95）、`/bff/analysis/ask`（RAG p95）、取り込みパイプライン（スループット・反映時間）。
- 対象外: 個別ロジックの単体性能、外部 LLM プロバイダ自体の SLA。

## ハーネス

- **k6**（`perf/k6/`）。`search-load.js`（p95<1500）・`rag-load.js`（p95<5000）は threshold 未達で非ゼロ終了する。
  取り込みスループット・反映時間は `perf/k6/README.md` の手順（パイプライン投入レート・検索ポーリング）で測る。
- 観察は Grafana（`knowledge-platform-overview`）＋ Prometheus SLO アラート（`deploy/prometheus/alerts.yml`・#198）＋ Tempo。

## テストケース一覧

| ID | 指標 | 手順 | 合格条件 | 区分 |
| --- | --- | --- | --- | --- |
| P-01 | 検索 p95 | `k6 run search-load.js`（本番相当データ・段階負荷） | `http_req_duration{scenario:search}` p(95) < 1500ms・失敗率 < 1% | 負荷（手動/環境） |
| P-02 | RAG 初回 p95 | `k6 run rag-load.js` | `http_req_duration{scenario:rag}` p(95) < 5000ms・失敗率 < 2% | 負荷（手動/環境） |
| P-03 | 取り込みスループット | N 件投入→`IngestionCompleted`/Qdrant points が N に達する時間 T を計測 | `N/T` ≥ 1 万件/時（必要ワーカー数を記録） | 負荷（手動/環境） |
| P-04 | 反映時間 | 一意語文書を投入→`/bff/search` ポーリングでヒットまで計測 | ≤ 15 分 | 負荷（手動/環境） |

## 実測記録

> 実測はデプロイ済み環境（compose / k3s stg）＋本番相当データ＋（RAG は）LLM 経路構成が必要（環境ブロック）。
> 実行のたびに下表へ追記する（日時・環境・負荷レベル・p95/スループット・合否）。

| 日付 | 環境 | 指標 | 負荷レベル | 実測 | 合否 | 備考 |
| --- | --- | --- | --- | --- | --- | --- |
| （未実施） |  |  |  |  |  | 環境準備後に実施（#196 OPEN 維持） |

## 追随（実測後に更新する）

- FR-03 受け入れ基準の未チェック 2 項（p95・15 分反映）を実測で更新する。
- HPA しきい値（#197 `scaling.hpa`）・リソース requests/limits・RRF k 値/候補数（FR-03 未決事項）を実測で調整する。
- 目標未達は改善タスクを分割起票する。

## 関連仕様

- ハーネス: `perf/k6/README.md`
- 機能: `../functional/FR-03_hybrid-search.md`、`../tests/FR-02_ingestion.md`、`../tests/FR-03_hybrid-search.md`
- 監視: `../operations/operations.md`（監視・アラート）、`deploy/prometheus/alerts.yml`
- スケール: `../adr/IADR-0050_hpa-pdb-scaling-scope.md`、#197
