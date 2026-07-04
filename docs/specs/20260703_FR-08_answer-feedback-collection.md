---
title: 作業仕様書 — FR-08 回答へのフィードバック（👍/👎・コメント）収集
type: work-spec
status: completed
related_ids:
  - FR-08
  - UC-01
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-08)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-01)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0002_service-boundaries-db-per-service.md"
related_specs:
  - ../functional/FR-08_answer-feedback.md
  - ../functional/FR-04_ai-answer-citations.md
  - ../tests/FR-08_answer-feedback.md
related_adrs:
  - ADR-0002 (DB per service。フィードバックは専用サービス・専用 DB で保持)
  - IADR-0010 (本 PR で新設。フィードバック専用サービスと 1 ユーザー 1 回答 upsert)
---

# 作業仕様書: FR-08 回答へのフィードバック収集

## 目的

FR-08「回答へのフィードバック（👍/👎・コメント）を収集し、品質改善に活用する」（UC-01）を実装する。
AI 回答（FR-04）に対して利用者が **👍/👎** と任意の **コメント** を送信し、
品質改善（傾向分析・低評価回答の抽出）に活用できるよう、フィードバックを永続化・集計する。

## 背景・現状（調査結果）

- `AiAnalysisService` の `/analysis/ask`（UC-01 の RAG 回答）が `AiAnswerDto` を返す。
  現状 `AiAnswerDto` に**回答を一意に識別する ID が無い**ため、フィードバックを回答へ紐付けられない。
- 既存サービス（`DataSourceService` / `WikiService`）は **EF Core + Postgres（DB-per-service, ADR-0002）**、
  最小 API エンドポイント、`MigrateAsync` による起動時スキーマ更新、ヘルスチェックという共通構成を持つ。
- BFF（`KnowledgePlatform.Bff`）は UC-01 のチャット画面向けに `AiAnalysisService` を集約している。

## 作業範囲

### 含むもの（本 PR）
- **`AiAnswerDto` に `AnswerId`（Guid）を付与**：回答ごとに一意な ID を発行し、フィードバックの紐付け先とする。
  既存の位置引数コンストラクタを壊さないよう `init` 既定値プロパティとして追加（各回答生成で自動採番）。
- **`FeedbackService`（新規マイクロサービス）**：ADR-0002 に従い専用 DB（`feedback_svc`）で保持。
  - `AnswerFeedback` エンティティ（`Id` / `AnswerId` / `Question` / `Rating`(up|down) / `Comment?` / `UserId` / `CreatedAt` / `UpdatedAt`）。
  - `POST /feedback`：フィードバック送信。`Rating` は必須・`up`/`down` のみ。`Comment` は最大 2000 文字。
    利用者は JWT から特定（テスト環境は `anonymous`）。**同一 (AnswerId, UserId) は upsert**（重複送信で二重計上しない）。
  - `GET /feedback`：品質改善向けの一覧（`rating` で絞り込み可）。低評価回答のレビューに用いる。
  - `GET /feedback/stats`：集計（👍/👎 件数・合計・満足率）。品質可視化（FR-10 ダッシュボード）の入力に用いる。
- **共有 DTO**：`FeedbackDto` / `FeedbackStatsDto` を `Shared.Contracts` に追加。
- **BFF 集約**：`/bff/feedback`（送信）・`/bff/feedback/stats`（集計）を追加し、UC-01 チャット画面から利用可能にする。
- **配備**：`docker-compose`（`feedback-service`）と DB 初期化（`feedback_svc`）に追加。独立デプロイ・ロールバック可能。
- **テスト**：送信・upsert・バリデーション・集計・ヘルス・BFF 集約。

### 含まないもの（別 PR / 別 FR）
- フィードバックを可視化する管理ダッシュボード本体（**FR-10**, UC-05）。本 PR は集計 API までを提供。
- フィードバックを用いた検索・回答ロジックの自動チューニング（学習ループ）。将来課題。
- 画面（SC）実装本体。SC 未設定のため、UI は BFF 契約提供に留める。

## 受け入れ基準の対応

Issue の受け入れ基準は FR-08 固有ではなく、UC-01 全体の共通基準（横断検索・ABAC・鮮度・独立デプロイ・p95）。
FR-08 の実質スコープ「フィードバック収集」に対応付けると以下。

| 受け入れ基準 | 対応 |
| --- | --- |
| ① 横断検索・出典 | 既存 RetrievalService/AiAnalysisService/BFF で担保（本 PR 対象外）。本 PR はその回答へフィードバックを付ける |
| ② 権限外は現れない | 本 PR は回答**後**のフィードバック収集で、文書内容を保持しない（`Question` と評価のみ）。ABAC 経路は不変更 |
| ③ 更新後の反映 | 対象外（フィードバックは検索索引の鮮度と独立） |
| ④ 個別デプロイ・ロールバック | **FeedbackService は独立サービス（専用 DB・Dockerfile・compose 定義）**。他サービス非改変（DTO 追加を除く） |
| ⑤ p95 レイテンシ | フィードバック送信は単一行 upsert、集計は索引集計で軽量。負荷実測は別作業 |

**FR-08 固有の完了条件**（本 PR で検証）:
- 利用者が 👍/👎・コメントを回答へ送信でき、永続化される。
- 同一利用者の同一回答への再送信は上書き（二重計上しない）。
- 品質改善に使えるよう、一覧と集計（満足率）を取得できる。
- 不正な評価値・過大コメントは 400 で拒否する。

## 実装方針（IADR 化した判断）
- **フィードバックは専用マイクロサービスで保持**（ADR-0002 準拠。AiAnalysisService はステートレスな回答生成に集中）。
- **1 ユーザー 1 回答 1 フィードバック（upsert）**：重複送信で満足率が歪まないよう冪等化。
- 以上を [IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md) に記録した。

## テスト観点
- 送信：`up`/`down` + コメントで 201/200、DB に保持される。
- upsert：同一 (AnswerId, UserId) の再送信で件数が増えず内容が更新される。
- バリデーション：不正 `Rating`・空 `AnswerId`・過大 `Comment` は 400。
- 集計：👍/👎 件数・満足率が正しい。
- ヘルス：`/health/live` が 200。
- BFF：`/bff/feedback` が FeedbackService へ委譲し 200/201 を返す。
