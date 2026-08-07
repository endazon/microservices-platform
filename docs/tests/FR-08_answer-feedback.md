---
title: テスト仕様書 — FR-08 回答へのフィードバック収集
type: test
status: in-progress
related_ids:
  - FR-08
  - UC-01
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-08)"
related_specs:
  - ../specs/20260703_FR-08_answer-feedback-collection.md
  - ../functional/FR-08_answer-feedback.md
---

# テスト仕様書: FR-08 回答へのフィードバック収集

| # | 観点 | 前提 | 操作 | 期待結果 | 実装 |
| --- | --- | --- | --- | --- | --- |
| T-01 | 送信（👍） | — | `POST /feedback {rating:"up"}` | 201。DB に 1 行、`Rating=up` | `PostUpFeedback_Creates` |
| T-02 | 送信（👎＋コメント） | — | `POST /feedback {rating:"down", comment}` | 201。`Comment` 保持 | `PostDownWithComment_Persists` |
| T-03 | upsert | 同一 (AnswerId, UserId) で送信済 | 再度 `POST` で `rating` 変更 | 200。件数増えず内容更新 | `SameUserSameAnswer_Upserts` |
| T-04 | 不正 rating | — | `POST {rating:"maybe"}` | 400 | `InvalidRating_Returns400` |
| T-05 | 空 answerId | — | `POST {answerId: empty guid}` | 400 | `EmptyAnswerId_Returns400` |
| T-06 | 過大コメント | — | `POST {comment: 2001 文字}` | 400 | `TooLongComment_Returns400` |
| T-07 | 集計 | 👍×2, 👎×1 保存済 | `GET /feedback/stats?answerId` | `up=2,down=1,total=3,rate≈0.667` | `Stats_ComputesSatisfaction` |
| T-08 | 一覧絞り込み | 👍/👎 混在 | `GET /feedback?rating=down` | 👎 のみ返る | `List_FiltersByRating` |
| T-09 | ヘルス | — | `GET /health/live` | 200 | `GetHealthLive_Returns200` |
| T-10 | BFF 集約 | — | `POST /bff/feedback` | 201/200（FeedbackService へ委譲） | `BffPostFeedback_Delegates` |
| T-11 | 回答 ID 付与 | — | `POST /analysis/ask` | `AiAnswerDto.AnswerId` が非空 | `AskAnswer_HasAnswerId` |
| T-12 | 同時 2 重送信 | 同一 (AnswerId, UserId) | 同時に 8 回 `POST` | いずれも 5xx を返さない（冪等・no-crash）※ | `ConcurrentDoubleSubmit_NoServerError` |
| T-13 | 一覧の認可 | 非管理ロール | `GET /feedback` | 403（Comment/UserId は AdminOnly） | `List_WithoutAdminRole_Returns403` |
| T-14 | 一覧ページング | 3 行以上 | `GET /feedback?take=2` | 2 件に制限される | `List_RespectsTakeLimit` |
| T-15 | **投稿の認証**（新） | 無認証 | `POST /feedback` / `POST /bff/feedback` | **401**（匿名投稿は許さない） | **未実装 —— #521** |
| T-16 | **統計の権限**（新） | 認証済・運用者/管理者以外 | `GET /feedback/stats` / `GET /bff/feedback/stats` | **403**（無認証は 401） | **未実装 —— #521** |

> **［2026-08-07 追記 / #586］T-15 / T-16 は計画が 2026-08-07 に追加した受け入れ基準の写像である。**
> planning `3e58b97`（PR planning#244〔裁定依頼 planning#236〕）で計画 FR-08 に
> 「**フィードバックの投稿端点が無認証で 401 を返す。統計の取得端点は、認証済みでも運用者・管理者以外には
> 403 を返す**」が受け入れ基準として加わった
> （[02_requirements](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) `:202`）。
> `CLAUDE.md` は「受け入れ基準をテストケースへ写像する」を必須としているため、**基準が増えた時点で
> T- 番号を採番して置く**（テストの実装は挙動の変更と同じ PR に属する）。
> **実装・現行の 4 端点への `RequireAuthorization` 追加・OpenAPI の `responses` 追加はいずれも #521 が持つ。**
> #586 は planning pin の更新と事実の追随に限る。同型の送り先つき記述:
> [機能仕様書 FR-08](../functional/FR-08_answer-feedback.md)・[通信仕様書](../api/BFF_bff-surface.md)・
> `docs/api/openapi.yaml`・[[IADR-0010]]・[[IADR-0131]]・`FeedbackEndpoints.cs`。

- 受け入れ基準（FR-08 固有）との対応: 収集=T-01/02、品質改善への活用=T-07/08、冪等=T-03/T-12、
  入力規則=T-04/05/06、独立サービス稼働=T-09、画面連携=T-10、紐付け=T-11、認可=T-13、ページング=T-14、
  **投稿の認証（無認証 401）=T-15、統計の権限（権限外 403）=T-16**（いずれも 2026-08-07 に計画が追加。**#521**）。
- ※ T-12 注記: InMemory プロバイダは一意インデックスを強制しないため `DbUpdateException` の
  フォールバック経路自体は再現されない（実 Postgres の統合環境で担保）。本テストは非アトミックな
  read-then-write でも未処理例外→500 を返さないこと（no-crash / 全 2xx）を保証する回帰テスト。
