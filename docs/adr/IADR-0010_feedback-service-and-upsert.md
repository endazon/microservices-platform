---
title: IADR-0010 回答フィードバックは専用サービスで保持し、1 ユーザー 1 回答は upsert で冪等化する
type: impl-adr
status: Accepted
related_ids:
  - FR-08
  - UC-01
author: claude
created: 2026-07-03
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-08)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-01)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0002_service-boundaries-db-per-service.md"
---

# IADR-0010: 回答フィードバックは専用サービスで保持し、1 ユーザー 1 回答は upsert で冪等化する

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: ADR-0002（サービス境界 / DB-per-service）、FR-08（回答へのフィードバック収集）、FR-04（AI 回答・出典）

## コンテキストと課題

FR-08 は「回答へのフィードバック（👍/👎・コメント）を収集し、品質改善に活用する」を求める。実装にあたり 2 点の判断が要った。

1. **フィードバックをどこに置くか**: AI 回答を生成する `AiAnalysisService` に相乗りさせるか、専用サービスにするか。
2. **同一利用者が同じ回答へ複数回フィードバックした場合の扱い**: 追記（複数行）か、上書き（1 行）か。

`AiAnswerDto` には回答を一意に識別する ID が無く、フィードバックの紐付け先が定義できないという前提課題もあった。

## 検討した選択肢

1. **専用 `FeedbackService`（専用 DB）＋ (AnswerId, UserId) upsert**。回答へは `AiAnswerDto.AnswerId` を新設して紐付ける。
2. `AiAnalysisService` にフィードバック用テーブル・API を相乗り。
3. フィードバックは追記のみ（同一利用者の再送信も別行として蓄積）。

## 決定

選択肢 1 を採用する。

- **専用サービス化**: ADR-0002（DB-per-service）に従い、フィードバックは独立した `FeedbackService`（専用 DB `feedback_svc`）で保持する。
  既存の `DataSourceService` / `WikiService` と同一構成（EF Core + Postgres、最小 API、起動時 `MigrateAsync`、ヘルスチェック）。
- **回答 ID の新設**: `AiAnswerDto` に `AnswerId`（Guid、`init` 既定値で自動採番）を追加し、フィードバックの紐付け先とする。
  既存の位置引数コンストラクタ・呼び出し箇所を壊さない。
- **upsert で冪等化**: `AnswerFeedback` は `(AnswerId, UserId)` に一意制約を持ち、同一利用者の同一回答への再送信は
  既存行を上書きする（`Rating` / `Comment` / `UpdatedAt` を更新）。
- **競合の扱い（TOCTOU）**: upsert は「事前確認（`FirstOrDefaultAsync`）＋ INSERT/UPDATE」の read-then-write で
  非アトミック。ほぼ同時の 2 重送信は両方が「既存なし」と判定して INSERT を試み、後勝ちが一意制約
  (`IX_Feedback_AnswerId_UserId`) 違反で `DbUpdateException` を投げる。これを **捕捉し、相手が作成した行を
  読み直して UPDATE へフォールバック**することで冪等（1 利用者 1 回答 1 行）を保証する。
  （`AuthorizationService` の属性辞書登録と同じ「事前確認 + 競合捕捉」パターン。真のアトミック upsert が要れば
  Npgsql の `ON CONFLICT DO UPDATE` へ移行可能だが、EF Core 標準機能で足りるため現行を採る。）
- **一覧の認可**: `GET /feedback` は自由記述 `Comment` と `UserId`（個人特定情報）を返すため、`AdminOnly`
  ポリシー（`platform-admin` ロール）で保護する。集計 `GET /feedback/stats` は件数・満足率のみで PII を含まず、
  BFF ダッシュボードが匿名集約するため認可を課さない。一覧は `skip`/`take`（既定 100・上限 500）でページングする。
  - > **［2026-08-07 追記・事実の更新 / #586］この「認可を課さない」は計画と食い違う。**
    > 計画 FR-08 が **「統計は運用者・管理者に限って参照できる」「投稿には認証を要する（匿名投稿は許さない）」**を
    > **確定**した（planning `3e58b97` = PR planning#244〔裁定依頼 planning#236〕。受け入れ基準は無認証 401・権限外 403）。
    > 本決定は「PII を含まないから認可を課さない」と判断したが、**計画は PII の有無ではなく参照者のロールで
    > 線を引いた**。**本追記は決定を変えない**——`RequireAuthorization` の追加は挙動の変更であり、
    > **是正は #521 が持つ**（#586 は planning pin の更新と事実の追随に限る）。
    > 同型の記述: [機能仕様書 FR-08](../functional/FR-08_answer-feedback.md)・
    > [通信仕様書](../api/BFF_bff-surface.md) §未決事項 3。

## 理由

- **専用サービス**: フィードバックは回答生成とはライフサイクル・スケール特性・改修頻度が異なる（書き込み主体、
  品質分析用の集計クエリ）。ADR-0002 の「サービスごとに DB を持ち、独立してデプロイ・ロールバックする」に沿い、
  受け入れ基準④（各サービスを個別にデプロイ・ロールバックでき、他サービスへ影響しない）を満たす。相乗り（選択肢 2）は
  回答生成サービスに書き込み負荷と別スキーマを持ち込み、境界を曖昧にする。
- **upsert**: 👍/👎 は「その回答に対する現在の評価」であり、同一利用者の重複送信で満足率が二重計上されると
  品質指標が歪む。1 ユーザー 1 回答 1 評価に冪等化することで、集計（満足率）が利用者数ベースで安定する。
  追記のみ（選択肢 3）は誤タップ・再送信でノイズが増え、品質改善の判断を誤らせる。

## 結果

- 良い影響: 受け入れ基準④（独立デプロイ・ロールバック）を満たす。フィードバックが回答単位で一意化され、
  満足率が安定する。集計 API は将来のダッシュボード（FR-10）の入力にそのまま使える。
- 悪い影響・トレードオフ: サービス数が 1 つ増える（運用・配備の対象増）。`AiAnswerDto` に ID を持たせたため、
  回答生成のたびに Guid を採番するが、コストは無視できる。
- フォローアップ:
  - フィードバックを可視化するダッシュボード本体は FR-10（UC-05）で実装する。本 PR は集計 API まで。
  - フィードバックを検索・回答チューニングへ環流する学習ループは将来課題。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260703_FR-08_answer-feedback-collection](../specs/20260703_FR-08_answer-feedback-collection.md)
