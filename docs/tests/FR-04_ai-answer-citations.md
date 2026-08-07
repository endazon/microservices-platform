---
title: AI 回答・出典提示 テスト仕様書
type: test-spec
status: draft
related_ids:
  - FR-04
  - FR-05
  - FR-11
  - UC-01
  - UC-02
  - SC-01
  - SC-08
  - IADR-0111
  - IADR-0131
author: claude
created: 2026-06-27
updated: 2026-08-06
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

- 対象: 出典写像ロジック（`CitationMapper`）、`/analysis/ask` 応答、`/bff/analysis/ask` 集約と Authorization 伝播、
  応答が名乗る使用モデル（`AiAnswerDto.Model` / `AskDoneEvent.Model`。[[IADR-0111]]）、
  **出典の機密区分**（`CitationDto.Confidentiality` と安全側への縮退。#541）。
- 対象外: 横断検索の権限制御の網羅（AuthorizationService 側で検証）、反映時間、負荷/p95。
  用途→モデルの解決そのもの（`LlmRouter` 側。FR-11 の T-02 / T-19）。

## テスト観点

- 正常系: 出典の連番採番、元文書リンク、回答＋出典の集約、送信成立時の実モデル名の透過、
  出典ごとの機密区分の写像。
- 境界/異常系: 検索結果 0 件、元文書リンク欠落時のフォールバック、抜粋の丸め、
  LLM を呼んでいない縮退応答が使用モデルを名乗らないこと、
  機密区分の欠落・空文字・未知値・大小文字違い。
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
| T-10 | ABAC 不許可（LLM 未呼出） | `RagOrchestrator.AskAsync` | `AiAnswerDto.Model` が空（`claude-opus-5` を名乗らない） | 使用モデルの正確性 | 自動 |
| T-11 | ABAC 不許可（LLM 未呼出） | `RagOrchestrator.AskStreamAsync` | `AskDoneEvent.Model` が空 | 使用モデルの正確性 | 自動 |
| T-12 | ゲートウェイが越境拒否（`sent=false`・`model=""`） | `AskAsync` / `AskStreamAsync` | `Model` が空（ゲートウェイ値を透過） | 使用モデルの正確性 | 自動 |
| T-13 | ゲートウェイ HTTP 失敗（非 2xx・未到達） | `AskAsync` / `AskStreamAsync` | `Model` が空 | 使用モデルの正確性 | 自動 |
| T-14 | 送信成立（`sent=true`・`model=claude-sonnet-5`） | `AskAsync` / `AskStreamAsync` | 実 route 結果をそのまま返す（回帰防止） | 使用モデルの正確性 | 自動 |
| T-15 | 呼び出し先不調（`sent=false`・`model=claude-sonnet-5`） | `AskAsync` / `AskStreamAsync` | route 結果を透過（空へ潰さない） | 使用モデルの正確性 | 自動 |
| T-16 | ゲートウェイが 2xx で本文 JSON `null`（逆シリアル化結果が null） | `AskAsync` | `Model` が空（`null` を応答契約へ載せない） | 使用モデルの正確性 | 自動 |
| T-15f | 分析結果の補足表示（SC-08） | `AnalysisDashboardPage` | `model` 空なら「モデル: 未使用（AI へ送信なし）」、非空ならモデル名 | 使用モデルの正確性 | 自動 |
| T-17 | 文書属性に機密区分あり（4 値） | `ToCitations` | `Confidentiality` に当該値が載る | 出典への機密区分 | 自動 |
| T-18 | 機密区分が欠落 / 空文字 / 空白 / 未知値 | `ToCitations` | `restricted` へ縮退（安全側） | 出典への機密区分 | 自動 |
| T-19 | 機密区分が `Internal`（大小文字違い） | `ToCitations` | `internal`（正準の小文字）へ正規化 | 出典への機密区分 | 自動 |
| T-20 | 区分の異なる 3 件（public / confidential / 欠落） | `ToCitations` | 出典ごとに `public` / `confidential` / `restricted` | 出典への機密区分 | 自動 |
| T-21 | — | `ConfidentialityLevels` / `new CitationDto(...7 引数)` | 値集合が 4 値・安全側の既定が `restricted`・区分を渡さない組み立ては `restricted` | 出典への機密区分（非破壊） | 自動 |

> T-17〜T-21 は `CitationMapperTests`（#541 / FR-04「出典には機密区分を含める」・SC-01 裁定 Q10）。
> **表示名（公開 / 社内限 / 秘 / 取扱制限）はテストしない**——正は計画リポジトリの用語集であり、
> 実装側で表示名を固定すると用語の正が 2 か所へ割れる。テストが固定するのは**値集合と縮退規則**だけである。
>
> T-10〜T-16 は `RagOrchestratorDegradedModelTests`（[[IADR-0111]] / #403）。T-15f は
> `AnalysisDashboardPage.test.tsx`。**LLM を呼んでいない縮退応答がモデル名を名乗らない**ことを固定する
> （以前は存在しない設定キー `Llm:DefaultModel` のフォールバックで常に `claude-opus-5` を返していた）。

## テストデータ

- `SearchResultDto`（タイトル・URI・本文を変えた 1〜3 件）。機密区分は `Attributes["confidentiality"]` で与える。
- BFF スタブハンドラが返す `AiAnswerDto`（出典 1 件）。

## 関連仕様

- 機能仕様書: `../functional/FR-04_ai-answer-citations.md` / `../functional/FR-11_llm-egress-routing.md`
- 作業仕様書: `../specs/20260627_FR-04_ai-answer-citations.md` / `../specs/20260728_issue-403_degraded-answer-model.md` / `../specs/20260806_issue-541_citation-confidentiality.md`
- 実装 ADR: `../adr/IADR-0111_degraded-answer-model-label.md`
- 画面仕様書: `../screens/SC-08_ai-analysis-dashboard.md`
- 通信仕様書: `../api/openapi.yaml`

## 未決事項

- E2E（実 LLM・実検索）での性能/p95 検証は負荷試験タスクで別途実施。
- SC-01 の出典行に機密区分チップを描く画面テストは、表示を作る issue で足す（本書の対象は契約と写像まで）。
