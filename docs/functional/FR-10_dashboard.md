---
title: 機能仕様書 — FR-10 利用状況・検索傾向・回答品質ダッシュボード
type: functional
status: in-progress
related_ids:
  - FR-10
  - UC-05
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-10)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-05)"
related_specs:
  - ../specs/20260703_FR-10_usage-dashboard.md
  - ../tests/FR-10_dashboard.md
  - ../functional/FR-08_answer-feedback.md
  - ../adr/IADR-0011_dashboard-service-usage-aggregation.md
---

# 機能仕様書: FR-10 利用状況・検索傾向・回答品質ダッシュボード

## 概要

運用・分析の担当者が、**利用状況**（検索・回答の件数推移）、**検索傾向**（よく検索される語）、
**回答品質**（👍/👎 の満足率）を 1 つのダッシュボードで把握できる。業務指標のドメイン集計は
`DashboardService`（専用マイクロサービス、ADR-0002）が担い、回答品質は FR-08 の `FeedbackService`
（`/feedback/stats`）を単一の出所として BFF が集約する（[IADR-0011](../adr/IADR-0011_dashboard-service-usage-aggregation.md)）。
ADR-0006 の Grafana（インフラ指標）とは責務が異なる（業務指標はアプリ側 API で提供）。

## データモデル（`UsageEvent`）

| 項目 | 型 | 説明 |
| --- | --- | --- |
| Id | Guid | 主キー |
| EventType | string(16) | `search`（検索実行）/ `answer`（AI 回答生成）。小文字正規化 |
| Query | string(512)? | 検索語（種別が `search` のときのみ保持。前後空白除去・小文字化。超過は切り詰め） |
| UserId | string(256) | 記録者（JWT の名前。テスト・開発は `anonymous`） |
| OccurredAt | DateTimeOffset | 発生時刻（UTC）。期間フィルタ・集計の基準 |

索引: `(OccurredAt, EventType)`（期間フィルタ・種別集計の効率化）。

## API（DashboardService）

| メソッド | パス | 認可 | 説明 |
| --- | --- | --- | --- |
| POST | `/dashboard/events` | 認証済み | 利用イベント記録。`EventType` 必須（`search`/`answer`）。201 |
| GET | `/dashboard/usage?days=N` | AdminOnly | 日次利用状況（日付 × 種別の件数） |
| GET | `/dashboard/trends?days=N&top=M` | AdminOnly | 検索傾向（検索語 × 件数の上位） |
| GET | `/dashboard/summary?days=N&top=M` | AdminOnly | 利用側サマリ（総件数・利用状況・検索傾向） |

- `days`：既定 7・上限 90 にクランプ。`top`：既定 10・上限 50 にクランプ（無制限集計を防ぐ）。
- 集計は UTC 当日 00:00 を含む起点から現在まで。日付は UTC で丸める。

## API（BFF 集約）

| メソッド | パス | 認可 | 説明 |
| --- | --- | --- | --- |
| GET | `/bff/dashboard/summary?days=N&top=M` | AdminOnly | DashboardService の利用側サマリと FeedbackService の回答品質を集約し `DashboardSummaryDto` を返す |

- BFF は DashboardService（AdminOnly）へ `Authorization` ヘッダを伝播する。
- 利用側サマリと回答品質は並行取得する（互いに独立）。後段が非 2xx ならそのステータスを透過する。

## DTO（`Shared.Contracts`）

- `UsageEventRequest(EventType, Query?)`
- `UsagePointDto(Date, EventType, Count)`
- `SearchTrendDto(Term, Count)`
- `DashboardUsageDto(TotalSearches, TotalAnswers, UsageTrend, TopSearchTerms)` — DashboardService の利用側サマリ
- `DashboardSummaryDto(TotalSearches, TotalAnswers, UsageTrend, TopSearchTerms, Quality)` — BFF が回答品質を付加
- `UsageEventType`（`search` / `answer` の定数・検証・正規化）

## バリデーション・例外

- `EventType` が `search`/`answer` 以外 → 400。
- 検索語は種別が `search` のときのみ集計対象（`answer` では保持しない）。空・空白のみは集計対象外。
- 集計 API を非管理ロールで呼ぶ → 403（AdminOnly）。
- `days`/`top` は範囲外でもクランプして常に有効値で集計する（エラーにしない）。

## 非機能・セキュリティ

- 集計値（件数・満足率・検索語）のみを扱い、文書本文・回答本文は保持・返却しない。
- 満足率は FeedbackService を単一の出所とし、DashboardService へ複製しない（乖離防止）。
- DashboardService は専用 DB・専用 Dockerfile・compose 定義を持ち、独立してデプロイ・ロールバックできる（受け入れ基準④）。

## 対象外（別 PR）

- 画面（SC）実装本体・グラフ描画（UI）。
- 検索・回答経路からの利用イベント自動送信の配線。
- LLM API コストの可視化（ADR-0006 フォローアップ）。
