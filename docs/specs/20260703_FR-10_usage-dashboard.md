---
title: 作業仕様書 — FR-10 利用状況・検索傾向・回答品質ダッシュボード
type: work-spec
status: completed
related_ids:
  - FR-10
  - UC-05
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-10)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-05)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0002_service-boundaries-db-per-service.md"
related_specs:
  - ../functional/FR-10_dashboard.md
  - ../functional/FR-08_answer-feedback.md
  - ../tests/FR-10_dashboard.md
related_adrs:
  - ADR-0006 (可観測性スタック。ダッシュボードは利用状況・検索傾向・回答品質を可視化)
  - ADR-0002 (DB per service。ダッシュボードは専用サービス・専用 DB で保持)
  - IADR-0011 (本 PR で新設。DashboardService と利用イベント集約)
  - IADR-0010 (FR-08。回答品質＝満足率の出所)
---

# 作業仕様書: FR-10 利用状況・検索傾向・回答品質ダッシュボード

## 目的

FR-10「利用状況・検索傾向・回答品質を可視化するダッシュボードを提供する」（UC-05）を実装する。
運用・分析の担当者が、**利用状況**（検索・回答の件数推移）、**検索傾向**（よく検索される語）、
**回答品質**（👍/👎 の満足率）を 1 つのダッシュボードで把握できるようにする。

## 背景・現状（調査結果）

- ADR-0006 は可観測性スタック（OTel/Prometheus/Loki/Tempo）を確定し、フォローアップに
  「ダッシュボード・アラートのテンプレート化」「外部 LLM API コストの可視化」を挙げる。ただしこれは
  **インフラ指標（SLO/SLI・技術メトリクス）** の可視化であり、FR-10 が求める **業務指標**
  （利用状況・検索傾向・回答品質）とは対象が異なる。FR-10 は業務指標をアプリ側で集計・提供する。
- 回答品質（満足率）は FR-08 で実装済みの `FeedbackService`（`/feedback/stats`）が集計値を提供する（PII 非含有）。
- 既存サービスは EF Core + Postgres（DB-per-service, ADR-0002）、最小 API、起動時 `MigrateAsync`、
  ヘルスチェックという共通構成。BFF が各サービスを集約して画面へ提供する。
- 現状、検索・回答の **利用イベントを業務集計向けに記録する場所が無い**（アクセスログはあるが、
  検索語のトップ集計・日次件数を返す API は無い）。

## 作業範囲

### 含むもの（本 PR）

- **`DashboardService`（新規マイクロサービス）**：ADR-0002 に従い専用 DB（`dashboard_svc`）で保持。
  - `UsageEvent` エンティティ（`Id` / `EventType`(search|answer) / `Query?` / `UserId` / `OccurredAt`）。
  - `POST /dashboard/events`：利用イベント記録。`EventType` は必須・`search`/`answer` のみ。検索語は
    種別が `search` のときのみ保持（前後空白除去・小文字化で正規化。最大 512 文字で切り詰め）。
    認証済みなら誰でも記録可（集計の入力を絞らない）。
  - `GET /dashboard/usage`：日次利用状況（日付 × 種別の件数）。**AdminOnly**。
  - `GET /dashboard/trends`：検索傾向（検索語 × 件数の上位）。**AdminOnly**。
  - `GET /dashboard/summary`：利用側サマリ（総件数・利用状況・検索傾向）を 1 応答で返す。**AdminOnly**。
  - 期間は `days`（既定 7・上限 90）、上位件数は `top`（既定 10・上限 50）でクランプする（無制限集計を防ぐ）。
- **共有 DTO**：`UsageEventRequest` / `UsagePointDto` / `SearchTrendDto` / `DashboardUsageDto` /
  `DashboardSummaryDto` / `UsageEventType` を `Shared.Contracts` に追加。
- **BFF 集約**：`/bff/dashboard/summary`（**AdminOnly**）を追加。DashboardService の利用側サマリと
  FeedbackService の回答品質（満足率）を並行取得し、`DashboardSummaryDto` に集約して画面へ返す。
- **配備**：`docker-compose`（`dashboard-service`）と DB 初期化（`dashboard_svc`）、solnx 登録。独立デプロイ・ロールバック可能。
- **テスト**：記録・バリデーション・集計（利用状況/検索傾向/正規化）・認可（AdminOnly/403）・ヘルス・BFF 集約。

### 含まないもの（別 PR / 別 FR）

- 画面（SC）実装本体。SC 未設定のため、UI は BFF 契約提供に留める（グラフ描画は将来のフロントで消費）。
- インフラ指標ダッシュボード（Grafana）そのもの。ADR-0006 のテンプレート化は運用側で対応。
- 検索・回答サービスからの利用イベント**自動送信**の配線。本 PR は記録 API と集計 API を提供し、
  呼び出し側（BFF/各サービス）からの発火は段階的に接続する（後方互換の追加のみ）。
- LLM API コストの可視化（ADR-0006 フォローアップ。LlmGateway 側のメトリクスで別途対応）。

## 受け入れ基準の対応

Issue の受け入れ基準は FR-10 固有ではなく UC 全体の共通基準（横断検索・ABAC・鮮度・独立デプロイ・p95）。
FR-10 の実質スコープ「可視化ダッシュボード」に対応付けると以下。

| 受け入れ基準 | 対応 |
| --- | --- |
| ① 横断検索・出典 | 対象外（本 PR は検索そのものではなく、その利用状況の可視化） |
| ② 権限外は現れない | ダッシュボードは集計値（件数・満足率・検索語）で文書本文を扱わない。集計 API は AdminOnly。文書 ABAC 経路は不変更 |
| ③ 更新後の反映 | 対象外（集計は検索索引の鮮度と独立） |
| ④ 個別デプロイ・ロールバック | **DashboardService は独立サービス（専用 DB・Dockerfile・compose 定義）**。他サービス非改変（DTO 追加・BFF 集約を除く） |
| ⑤ p95 レイテンシ | 記録は単一行 INSERT、集計は期間索引 (OccurredAt, EventType) 前提の軽量集計。負荷実測は別作業 |

**FR-10 固有の完了条件**（本 PR で検証）:
- 利用イベント（検索・回答）を記録でき、日次件数・検索語トップに集計できる。
- 回答品質（満足率）を含むサマリを 1 応答で取得できる（BFF 集約）。
- 集計 API は管理者ロールに限定される（運用情報の保護）。
- 不正な種別は 400、期間・上位件数は既定・上限にクランプされる。

## 実装方針（IADR 化した判断）

- **業務指標ダッシュボードは専用マイクロサービスで集計**（ADR-0002 準拠。ADR-0006 のインフラ指標とは責務分離）。
- **回答品質は FeedbackService を単一の出所とし複製しない**（BFF で集約）。
- **集計は AdminOnly**、記録は認証済みユーザに開放。**期間・上位件数はクランプ**して無制限集計を防ぐ。
- 以上を [IADR-0011](../adr/IADR-0011_dashboard-service-usage-aggregation.md) に記録した。

## テスト観点

- 記録：`search`（検索語つき）/`answer` で 201、DB に保持される。非管理ロールでも記録可。
- バリデーション：不正 `EventType` は 400。
- 集計（利用状況）：種別ごとの日次件数が正しい。
- 集計（検索傾向）：件数降順の上位語を返す。前後空白・大小の揺れは同一語に正規化される。
- 認可：集計 API は非管理ロールで 403。
- ヘルス：`/health/live` が 200。
- BFF：`/bff/dashboard/summary` が DashboardService と FeedbackService を集約し、資格情報を後段へ伝播する。非管理ロールは 403。
