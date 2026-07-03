---
title: テスト仕様書 — FR-10 利用状況・検索傾向・回答品質ダッシュボード
type: test
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
  - ../functional/FR-10_dashboard.md
  - ../adr/IADR-0011_dashboard-service-usage-aggregation.md
---

# テスト仕様書: FR-10 利用状況・検索傾向・回答品質ダッシュボード

## 対象・方針

- `DashboardService.Api.Tests`：記録・バリデーション・集計・認可・ヘルス。集計はグローバルのため、各テストは
  専用の InMemory DB（`TestWebApplicationFactory` を per-test 生成）で独立させる。
- `KnowledgePlatform.Bff.Tests`：BFF `/bff/dashboard/summary` の集約・資格情報伝播・認可。DashboardService と
  FeedbackService はスタブハンドラで差し替える。認可は `TestAuthHandler`（既定 `platform-admin`）で検証する。

## テストケース

| ID | 対象 | 内容 | 期待 |
| --- | --- | --- | --- |
| T-01 | DashboardService | `search`（検索語つき）を記録 | 201 |
| T-02 | DashboardService | `ANSWER`（大文字）を記録 | 201（正規化） |
| T-03 | DashboardService | 不正な `EventType`（`click`） | 400 |
| T-04 | DashboardService | 検索×2・回答×1 → 利用状況集計 | search 合計 2・answer 合計 1 |
| T-05 | DashboardService | 同一語×3・別語×1 → 検索傾向 | 件数降順、先頭が該当語（count=3） |
| T-06 | DashboardService | `Foo`/` foo `/`FOO` → 検索傾向 | 1 語 `foo`（count=3、正規化） |
| T-07 | DashboardService | サマリ集約 | 総件数・利用状況・検索傾向が整合 |
| T-08 | DashboardService | 非管理ロールで `/dashboard/usage` | 403（AdminOnly） |
| T-09 | DashboardService | 非管理ロールで `POST /dashboard/events` | 201（記録は開放） |
| T-10 | BFF | `/bff/dashboard/summary` 集約 | 利用状況・検索傾向・回答品質を集約して返す |
| T-11 | BFF | 非管理ロールで `/bff/dashboard/summary` | 403（AdminOnly） |
| — | BFF | 資格情報伝播 | `Authorization` を DashboardService へ伝播 |
| — | 両サービス | `/health/live` | 200 |

## 受け入れ基準との対応

- 業務指標（利用状況・検索傾向・回答品質）の集計・提供 … T-04〜T-07, T-10。
- 運用情報の保護（AdminOnly）… T-08, T-11。
- 独立稼働（受け入れ基準④）… ヘルスチェック。
- 入力バリデーション … T-03。
