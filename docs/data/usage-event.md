---
title: 利用イベント（UsageEvent） データ仕様書
type: data-spec
status: in-progress
created: 2026-07-04
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-10]
adrs: [ADR-0006]
iadrs: []
specs: [01_requirements, ADR-0006_observability-otel-prom-loki]
issues: []
-->

# データ仕様書: 利用イベント（UsageEvent）

> DashboardService が所有する、利用状況・検索傾向・回答品質の集計元となる利用イベントを扱う。

## 起点となる計画書（トレーサビリティ）

- **関連機能要求(FR)**: FR-10（利用状況ダッシュボード＝利用状況・検索傾向・回答品質）
- **技術検討(06_technical)・ADR**:
  - ADR-0006 可観測性（OpenTelemetry / Prometheus / Loki）
  - 関連: ADR-0002 DB per Service（DashboardService 専用 DB）
- **計画書リンク**: `01_requirements.md`（計画リポ）

## 概要

UsageEvent は「検索実行」「AI 回答生成」といった利用イベントを 1 行として蓄積する単一エンティティである。日次件数（利用状況）・トップ検索語（検索傾向）などの集計元になる。集計は保存済み UsageEvent 群に対するクエリで行い、事前集計テーブルは持たない（現状のスキーマ上は生イベントのみ）。

## エンティティ定義

### UsageEvent（テーブル `UsageEvents`）

| 属性 | 型 | 必須 | 制約（一意/既定値/範囲） | 説明 |
| --- | --- | --- | --- | --- |
| Id | Guid (uuid) | ○ | 主キー。既定 `Guid.NewGuid()` | イベント識別子 |
| EventType | string (varchar(16)) | ○ | 最大長 16。値: `search` / `answer`（小文字正規化済み） | 利用イベント種別 |
| Query | string? (varchar(512)) | - | 最大長 512（`MaxQueryLength`）。超過分は切り詰め | 検索語（検索傾向の集計に使用） |
| UserId | string (varchar(256)) | ○ | 最大長 256 | 実行利用者 |
| OccurredAt | DateTimeOffset (timestamptz) | ○ | 既定 `UtcNow`。`(OccurredAt, EventType)` インデックス対象 | 発生時刻 |

## ER 図

```mermaid
erDiagram
    USAGE_EVENT {
        uuid Id PK
        varchar EventType
        varchar Query
        varchar UserId
        timestamptz OccurredAt
    }
```

> 単一テーブル。ダッシュボードの集計値（日次件数・トップ検索語・回答品質）はこの生イベントへのクエリ結果として算出され、集計専用テーブルは現状存在しない。

## キー・インデックス・関連

| 種別 | 対象 | 定義 |
| --- | --- | --- |
| 主キー | `UsageEvents.Id` | `HasKey(u => u.Id)` |
| インデックス | `UsageEvents (OccurredAt, EventType)` | `IX_UsageEvents_OccurredAt_EventType`（非一意）— 期間フィルタ・種別集計を効率化 |
| 外部キー | なし | UserId は越境参照（FK なし） |

## 整合性・制約ルール

- **種別の正規化**: `EventType` は `search` / `answer` に小文字正規化してから保存（カラム長 16 と整合）。
- **検索語の長さ制限**: `Query` は保存前に 512 文字へ切り詰め（`Truncate`）。集計対象の検索語として保持。
- **集計クエリ最適化**: `(OccurredAt, EventType)` インデックスにより、期間指定＋種別ごとの日次件数・傾向集計を効率化。
- **一意制約なし**: 同一利用者・同一時刻の重複記録は許容（生イベントログ）。

## 永続化方針

- **DB**: PostgreSQL、EF Core（`DashboardDbContext`）。ADR-0002 に従い DashboardService 専用 DB。
- JSON カラムなし（全カラムがスカラ／文字列）。
- 事前集計（マテビュー等）は未導入。集計は API 呼び出し時にクエリで実施する方針。
- メトリクス・トレースは ADR-0006（OTel/Prometheus/Loki）の可観測性基盤で別途扱い、本テーブルは業務的な利用イベントを保持する。

## マイグレーション・初期データ

- `20260703010000_InitialCreate` — `UsageEvents` テーブル・`IX_UsageEvents_OccurredAt_EventType`（非一意）作成。
- 初期データ（シード）なし。

## 関連仕様

- 機能仕様書: `../functional/FR-10_dashboard.md`
- ログ・可観測性仕様書: `../observability/`（存在する場合）
- 通信仕様書: `../api/openapi.yaml`
- 技術要件書: `../tech/tech-requirements.md`
- 関連データ仕様: `./feedback.md`（回答品質のフィードバック源）

## 未決事項

- 回答品質の集計は UsageEvent（`answer`）と AnswerFeedback（別サービス）の突合が前提だが、その連携方式（イベント／横断集計）は未確定。
- イベントの保持期間（リテンション）・アーカイブ・剪定方針は未定。
- 高頻度書き込み時のパーティショニング・時系列最適化（例: 月次パーティション）は未検討。
