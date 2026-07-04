---
title: AI 回答・出典提示 テスト仕様書
type: test-spec
status: draft
related_ids:
  - FR-04
  - UC-01
  - UC-02
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# テスト仕様書: AI 回答・出典提示

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04
- ユースケース（UC）: UC-01, UC-02
- 受け入れ基準の所在（02_requirements）: `02_requirements/01_requirements.md`
- 計画書リンク: 同上 / `07_adr/ADR-0010`

## テスト対象・範囲

- 対象: 出典写像ロジック（`CitationMapper`）、`/analysis/ask` 応答、`/bff/analysis/ask` 集約と Authorization 伝播。
- 対象外: 横断検索の権限制御の網羅（AuthorizationService 側で検証）、反映時間、負荷/p95、画面。

## テスト観点

- 正常系: 出典の連番採番、元文書リンク、回答＋出典の集約。
- 境界/異常系: 検索結果 0 件、元文書リンク欠落時のフォールバック、抜粋の丸め。
- セキュリティ: 利用者資格情報（Authorization）の後段伝播。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | 検索結果 3 件 | `CitationMapper.ToCitations` | 出典番号が 1,2,3 の連番 | 出典提示 | 自動 |
| T-02 | `MarkdownUri` あり | 同上 | `SourceUri` が Markdown URI | 元文書リンク | 自動 |
| T-03 | `MarkdownUri` なし | 同上 | `SourceUri` が `/documents/{id}` | 元文書リンク | 自動 |
| T-04 | 長文チャンク | 同上 | 抜粋が丸められ末尾 `…` | 出典提示 | 自動 |
| T-05 | 出典 2 件 | `BuildContext` | 文脈の `[1][2]` が出典番号と一致 | 採番一致 | 自動 |
| T-06 | 検索結果 0 件 | `ToCitations([])` | 出典空 | 例外フロー | 自動 |
| T-07 | スタブ回答 | `POST /analysis/ask` | 200・回答本文・出典あり | 出典提示 | 自動 |
| T-08 | スタブ後段 | `POST /bff/analysis/ask` | 200・出典を集約して返す | BFF 集約 | 自動 |
| T-09 | Bearer 付与 | `POST /bff/analysis/ask` | 後段へ `Authorization` 伝播 | 権限制御 | 自動 |

## テストデータ

- `SearchResultDto`（タイトル・URI・本文を変えた 1〜3 件）。
- BFF スタブハンドラが返す `AiAnswerDto`（出典 1 件）。

## 関連仕様

- 機能仕様書: `../functional/FR-04_ai-answer-citations.md`
- 作業仕様書: `../specs/20260627_FR-04_ai-answer-citations.md`
- 通信仕様書: `../api/openapi.yaml`

## 未決事項

- E2E（実 LLM・実検索）での性能/p95 検証は負荷試験タスクで別途実施。
