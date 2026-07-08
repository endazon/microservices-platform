---
title: SC-08 AI分析ダッシュボード テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-08
  - UC-02
  - FR-07
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-08_ai-analysis-dashboard.md"
  - "../specs/20260708_issue-134_sc08-ai-analysis-dashboard.md"
---

# テスト仕様書: SC-08 AI分析ダッシュボード

> 計画の受け入れ基準（Issue #134）と UC-02 のフロー（基本・例外）をテストケースへ写像する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-07 / FR-05
- ユースケース（UC）: UC-02
- 受け入れ基準の所在: Issue #134 ／ `docs/specs/20260708_issue-134_sc08-ai-analysis-dashboard.md`

## テスト対象・範囲

- 対象: SC-08 画面（`features/sc08-analysis`）の入力・送信ペイロード・結果表示・空縮退・異常系。
- 対象外: `/bff/analysis/analyze` のサーバ側テスト（既存）。LLM ゲートウェイ。

## テスト観点

- 基本フロー（UC-02）: 範囲を指定し分析依頼→回答＋出典表示。
- 送信ペイロード: instruction / taskType / range（query・topK・attributeFilters）が契約どおり組み立てられる。
- 例外フロー（UC-02 存在秘匿）: 権限外→空回答へ縮退→中立メッセージ（権限有無を開示しない）。
- バリデーション: instruction 空は実行不可。
- 異常系: 5xx/network→alert。403/404→中立。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | 認証済み | instruction 入力→実行、200(answer+citations) | 回答本文と番号付き出典が表示 | 結果・出典表示 | 自動(単体) |
| T-02 | 認証済み | taskType=比較・range(query/topK/属性)指定→実行 | 送信 body が `{instruction,taskType:'Compare',range:{query,topK,attributeFilters}}` | 範囲指定 | 自動(単体) |
| T-03 | citations に sourceUri あり | 結果表示 | 出典にリンク（href=sourceUri） | 出典表示 | 自動(単体) |
| T-04 | 権限外→空回答 | 200(answer=''，citations=[]) | 「該当する情報が見つかりませんでした。」中立表示 | 存在秘匿 | 自動(単体) |
| T-05 | instruction 空 | 実行ボタン | 無効（送信されない） | 入力規則 | 自動(単体) |
| T-06 | サーバエラー | 実行で 500 | `role="alert"` 実行失敗表示 | 異常系 | 自動(単体) |
| T-07 | 未認証 | `/analysis` を開く | `/login` へ誘導 | ルート登録・認証ガード | 自動(E2E) |

## テストデータ

- `AiAnswerDto` ダミー（answer に `[1]` マーカー、citations に 1 件）。
- 空縮退ダミー（answer=''、citations=[]）。

## 関連仕様

- 画面仕様書: `docs/screens/SC-08_ai-analysis-dashboard.md`
- 作業仕様書: `docs/specs/20260708_issue-134_sc08-ai-analysis-dashboard.md`

## 未決事項

- なし
