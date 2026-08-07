---
title: 機能仕様書 — FR-08 回答へのフィードバック収集
type: functional
status: in-progress
related_ids:
  - FR-08
  - UC-01
author: claude
created: 2026-07-03
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-08)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-01)"
related_specs:
  - ../specs/20260703_FR-08_answer-feedback-collection.md
  - ../tests/FR-08_answer-feedback.md
  - ../adr/IADR-0010_feedback-service-and-upsert.md
---

# 機能仕様書: FR-08 回答へのフィードバック収集

## 概要

AI 回答（FR-04, UC-01）に対し、利用者が **👍（up）/ 👎（down）** と任意の **コメント** を送信できる。
収集したフィードバックは、品質改善（低評価回答のレビュー・満足率の可視化）に活用する。
`FeedbackService`（専用マイクロサービス、ADR-0002）が保持・集計する。

## データモデル（`AnswerFeedback`）

| 項目 | 型 | 説明 |
| --- | --- | --- |
| `Id` | Guid | フィードバック ID |
| `AnswerId` | Guid | 対象 AI 回答の ID（`AiAnswerDto.AnswerId`） |
| `Question` | string(1000) | 回答の元となった質問（品質レビュー用の文脈。任意） |
| `Rating` | string(4) | `up` / `down` のみ |
| `Comment` | string(2000)? | 自由記述（任意） |
| `UserId` | string(256) | 送信者（JWT の識別子。テストは `anonymous`） |
| `CreatedAt` | timestamptz | 作成時刻 |
| `UpdatedAt` | timestamptz | 更新時刻 |

- 一意制約: `(AnswerId, UserId)`。同一利用者の同一回答は 1 行に upsert（[IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md)）。

## API（`FeedbackService`）

| メソッド | パス | 説明 |
| --- | --- | --- |
| POST | `/feedback` | フィードバック送信（新規は 201、既存更新は 200） |
| GET | `/feedback?rating=down&answerId=…&skip=…&take=…` | 一覧（**AdminOnly**。品質レビュー用。`rating`/`answerId` 絞り込み・`skip`/`take` ページング。既定 100・上限 500 件） |
| GET | `/feedback/stats?answerId=…` | 集計（👍/👎 件数・合計・満足率）。`answerId` 省略で全体集計。集計値のみ・PII 無しのため認可なし（**現在の実装の事実**。下記 2026-08-07 追記のとおり計画と食い違う） |

> **［2026-08-07 追記 / #586］計画 FR-08 は認可を確定した。上表の「認可なし」は計画と食い違う。**
> planning `3e58b97`（PR planning#244。裁定依頼 planning#236 の反映）で
> [02_requirements](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) の
> FR-08 に次が**確定**として追加された——**投稿には認証を要する（匿名投稿は許さない）／統計は運用者・
> 管理者に限って参照できる／受け入れ基準は「投稿端点が無認証で 401」「統計端点は認証済みでも権限外は 403」
> 「同一利用者が 2 回投稿しても集計は 1 件のまま」**。
> **上表と下記「例外フロー」の記述は、いまの実装の事実としては正しい**ため書き換えない。
> **是正（`RequireAuthorization` の追加とテスト）は #521 が持つ**——挙動の変更を伴うため独立した PR が要る
> （#586 は planning pin の更新と事実の追随に限る。[作業仕様書 #586](../specs/20260807_issue-586_planning-pin-adr-accepted.md) §対象外）。
> 関連する同型の記述: [通信仕様書](../api/BFF_bff-surface.md) §エンドポイント一覧・§未決事項 3、
> [[IADR-0010]]。

BFF 集約（UC-01 チャット画面向け）:

| メソッド | パス | 委譲先 |
| --- | --- | --- |
| POST | `/bff/feedback` | `FeedbackService POST /feedback` |
| GET | `/bff/feedback/stats` | `FeedbackService GET /feedback/stats` |

### リクエスト（POST /feedback）
```json
{ "answerId": "…guid…", "rating": "up", "comment": "根拠が明確で助かった", "question": "…" }
```

### 集計レスポンス（GET /feedback/stats）
```json
{ "up": 12, "down": 3, "total": 15, "satisfactionRate": 0.8 }
```
`satisfactionRate = up / total`（total=0 のとき 0）。

## バリデーション（入力規則）

- `rating` は必須。`up` / `down` 以外（大小無視）は 400。
- `answerId` は空 Guid（`00000000-…`）を拒否し 400。
- `comment` は 2000 文字超で 400。
- `question` は 1000 文字を超える分は保持時に切り詰める（送信は拒否しない）。

## 例外フロー

- 同一 `(AnswerId, UserId)` の再送信: 追加せず既存を上書き（二重計上しない）。
- 同一 `(AnswerId, UserId)` の**同時 2 重送信**（ダブルクリック・再試行）: 後勝ちの INSERT が一意制約違反となるが、
  `DbUpdateException` を捕捉して既存行の更新へフォールバックし、冪等を保つ（500 を返さない。[IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md)）。
- 一覧 `GET /feedback` への非管理ロールアクセス: 403（`Comment`/`UserId` を含むため `AdminOnly`）。
- 未認証（JWT 無し）: 開発・テスト環境では `anonymous` として受理。本番は認可基盤（ADR-0004）の下で識別子を得る。
  - > **［2026-08-07 追記 / #586］計画 FR-08 は「投稿には認証を要する（匿名投稿は許さない）」を確定した**
    > （planning `3e58b97` = PR planning#244〔裁定依頼 planning#236〕）。**受け入れ基準は「投稿端点が無認証で 401」**であり、
    > 本行の `anonymous` 受理は計画と食い違う。**是正は #521**（挙動の変更を伴うため独立した PR）。
    > 本行は「現在の実装の事実」としては正しいため書き換えていない。

## トレーサビリティ

- 実装: `src/knowledge/backend/Services/FeedbackService/`、`src/platform/backend/Bff/**/FeedbackBffEndpoints.cs`、
  `src/platform/backend/Shared/**/Dtos/FeedbackDto.cs`、`AiAnswerDto.AnswerId`。
- テスト: [test spec](../tests/FR-08_answer-feedback.md)。
- 決定: [IADR-0010](../adr/IADR-0010_feedback-service-and-upsert.md)。
