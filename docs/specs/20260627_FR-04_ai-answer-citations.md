---
title: 作業仕様書 — FR-04 AI 回答の出典（元文書リンク）提示
type: work-spec
status: completed
related_ids:
  - FR-04
  - UC-01
  - UC-02
  - FR-05
author: claude
created: 2026-06-27
updated: 2026-06-27
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../functional/FR-04_ai-answer-citations.md
  - ../tests/FR-04_ai-answer-citations.md
related_adrs:
  - ADR-0010 (RAG / AI 回答方式)
---

# 作業仕様書: FR-04 AI 回答の出典提示

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（検索結果を根拠に AI が要約・回答を生成し、出典＝元文書へのリンクを提示）
- ユースケース（UC）: UC-01, UC-02
- 画面（SC）: 未設定
- 関連 ADR: ADR-0010
- 計画書リンク: `02_requirements/01_requirements.md`

## 目的・背景

既存の `AiAnalysisService` には RAG フロー（ABAC スコープ解決 → ハイブリッド検索 → LLM 回答生成）が
実装済みだが、FR-04 の核心である「**出典（元文書へのリンク）の提示**」が弱い。
回答に紐づく番号付き出典を構造化し、利用者が元文書まで辿れるようにする。あわせて BFF の
`/bff/analysis/ask` を AiAnalysisService 連携で実装し、画面（将来）から単一窓口で利用できるようにする。

## 対象範囲

- 対象:
  - 出典 DTO `CitationDto`（番号・文書ID・タイトル・チャンクID・元文書リンク・スコア・抜粋）の追加
  - `AiAnswerDto.Citations` を `CitationDto` 列へ精緻化
  - 検索結果→番号付き出典の写像ロジック `CitationMapper`（回答本文の [1][2] と採番一致）
  - BFF `/bff/analysis/ask` の実装（AiAnalysisService 連携・Authorization 伝播）
  - 単体・統合テスト
- 対象外:
  - 画面（SC 未設定）／ストリーミング応答
  - 検索インデックス反映時間（FR-02/FR-03 側で担保）
  - 負荷試験（別タスク）

## 設計

```mermaid
flowchart LR
  UI -->|POST /bff/analysis/ask| BFF
  BFF -->|Authorization 伝播| AIS[AiAnalysisService /analysis/ask]
  AIS --> AUTHZ[AuthorizationService /authz/scope]
  AIS --> RET[RetrievalService /search]
  AIS --> LLM[LlmGateway /complete]
  AIS -->|AiAnswerDto = Answer + CitationDto[]| BFF
```

- `CitationMapper.ToCitations(results)`: 検索結果を 1 始まりの番号付き出典へ変換。`MarkdownUri` を
  元文書リンクとして優先し、無ければ `/documents/{DocumentId}` を返す。抜粋は 240 文字で丸める。
- `CitationMapper.BuildContext(citations)`: LLM へ渡す参照文書文脈を出典番号と一致させて生成。
- `RagOrchestrator`: 上記マッパを用いて回答と出典を返す。LLM 不調時は縮退（出典のみ提示）。
- BFF: AiAnalysisService へ転送し `AiAnswerDto` を返す。ABAC のため Authorization を後段へ伝播。

## 受け入れ基準（本作業で満たす範囲）

- [ ] AI 回答に番号付き出典が付き、各出典が元文書リンク（`SourceUri`）を持つ。
- [ ] 出典番号と回答本文・LLM 文脈の採番が一致する。
- [ ] 権限解決のため BFF が Authorization を後段へ伝播する（権限外文書の混入を後段で排除）。
- [ ] `/bff/analysis/ask` が AiAnalysisService の回答＋出典を集約して返す。

## テスト方針

- 単体: `CitationMapper`（採番・リンクのフォールバック・抜粋丸め・文脈採番一致）。
- 統合: `/analysis/ask`（出典付き回答）, `/bff/analysis/ask`（集約・Authorization 伝播）。
- 詳細は `../tests/FR-04_ai-answer-citations.md`。

## 計画書との差異

- 差異: なし（計画書の受け入れ基準のうち、横断検索の権限制御・反映時間・個別デプロイ・p95 は
  既存基盤および別タスクで担保。本作業は「出典提示」と「BFF 集約」に限定）。

## 未決事項

- `SourceUri` の最終形式（文書詳細画面の確定 URL）は SC 確定後に再調整する。
