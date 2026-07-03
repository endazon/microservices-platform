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

- 受け入れ基準（FR-08 固有）との対応: 収集=T-01/02、品質改善への活用=T-07/08、冪等=T-03、
  入力規則=T-04/05/06、独立サービス稼働=T-09、画面連携=T-10、紐付け=T-11。
