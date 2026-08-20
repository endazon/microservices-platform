---
title: AI 回答・出典提示 機能仕様書
type: functional-spec
status: draft
created: 2026-06-27
updated: 2026-08-06
author: claude
---
<!-- trace:
ids: [FR-04, FR-05, FR-11, SC-01, SC-08, UC-01, UC-02]
adrs: []
iadrs: [IADR-0037, IADR-0111, IADR-0131, IADR-0132]
specs: [01_requirements]
issues: [#201]
-->

# 機能仕様書: AI 回答・出典提示

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04
- ユースケース（UC）: UC-01, UC-02
- 業務フロー（04_workflows）: 横断検索 → 根拠提示付き AI 回答
- 計画書リンク: `02_requirements/01_requirements.md`、`07_adr/ADR-0010`

## 概要

利用者の質問に対し、権限内のデータソースを横断検索した結果を根拠として AI が回答を生成し、
回答の各記述に対応する**番号付き出典（元文書へのリンク）** を提示する。利用者は出典から
元文書へ辿り、回答の妥当性を自分で検証できる。

## 機能詳細

| 項目 | 内容 |
| --- | --- |
| 入力 | `question`（必須）, `scope`（任意） / 利用者の資格情報（JWT クレーム: clearance, department） |
| 処理 | ABAC スコープ解決 → ABAC フィルタ付きハイブリッド検索（TopK=5）→ 検索結果を番号付き出典へ写像 → 出典文脈で LLM 回答生成 |
| 出力 | `AiAnswerDto`（`Answer`, `Citations[]`, `Model`, `InputTokens`, `OutputTokens`）。ストリーミングは `AskDoneEvent`（`AnswerId`, `Model`, `InputTokens`, `OutputTokens`） |
| 業務ルール | **`Model` は実際に使用したモデルのみを名乗る**。値の出所は LLM ゲートウェイの報告値だけで、呼び出し側は決めない。LLM を呼んでいない縮退（ABAC 不許可・機密区分による送信拒否・ゲートウェイ不達）では**空文字＝モデル未使用**を返す。出典番号は 1 始まり連番。回答本文の `[n]` と出典 `Number` を一致させる。元文書リンクは正規化 Markdown URI を優先し、無ければ `/documents/{id}`。根拠の無い情報は回答に含めない。 |

### CitationDto（出典）

| フィールド | 意味 |
| --- | --- |
| `Number` | 出典番号（1 始まり、回答本文 `[n]` と対応） |
| `DocumentId` / `ChunkId` | 元文書・該当チャンクの識別子 |
| `DocumentTitle` | 元文書タイトル |
| `SourceUri` | 元文書へのリンク（Markdown URI もしくは `/documents/{id}`） |
| `Score` | 検索スコア |
| `Snippet` | 該当箇所の抜粋（240 文字で丸め） |
| `Confidentiality` | **その文書の機密区分**（ABAC 文書属性 `confidentiality`）。値集合は `public` / `internal` / `confidential` / `restricted`（**`enum` にしない**。[[IADR-0131]] 決定 5）。#541 |

**`Confidentiality` の供給と縮退（#541・FR-04「出典には機密区分を含める」）**

- 供給元は `SearchResultDto.Attributes` の `confidentiality`（実在の ABAC 属性キー）である。
- **属性の欠落・空文字・未知値は安全側（`restricted`）へ縮退する**（`08_data-egress-policy`「既定は安全側」/
  FR-05 deny-by-default）。過剰公開は「社外資料へ引用してよいか」の判断を誤らせるため、過剰制限へ倒す。
- 出典に載る区分と、LLM ゲートウェイへ渡す最高機密区分は**同じ規則**
  （`ConfidentialityLevels`）から導く。画面に「公開」と出ているのにゲートウェイは `restricted` として
  扱う、という食い違いを作らない。
- **表示名（公開 / 社内限 / 秘 / 取扱制限）は本リポジトリで定義しない。** 正は計画リポジトリの用語集
  （`planning/docs/glossary.md`）である。**`restricted` は「取扱制限」であって「極秘」ではない。**

## 処理フロー / 状態遷移

```mermaid
flowchart TD
  A[質問受信] --> B[ABAC スコープ解決]
  B --> C[ABAC フィルタ付き検索]
  C --> D[検索結果→番号付き出典へ写像]
  D --> E{LLM 応答}
  E -->|成功| F[回答 + 出典を返す]
  E -->|失敗/縮退| G[出典のみ提示し縮退メッセージ]
```

## 例外・エラー処理

| 条件 | 振る舞い | エラー表示 |
| --- | --- | --- |
| 検索結果 0 件 | 出典空・該当なしメッセージ | 「関連する情報が見つかりませんでした。」 |
| LLM 不調 | 検索結果（出典）のみ提示する縮退。`Model` は空（未到達） | 「LLM が現在利用できないため、関連文書の一覧を返します。」 |
| 機密区分により送信拒否（FR-11 `Sent=false`） | 外部送信せず出典のみ提示する縮退。`Model` はゲートウェイ報告値（未呼出なら空、呼び出しを試みて失敗した場合は実 route 結果） | 「機密区分により AI 送信を行わなかったため、関連文書の一覧を返します。」 |
| 閲覧可能文書なし（FR-05 deny-by-default） | 検索・LLM を呼ばず空回答へ縮退。`Model` は空 | 「閲覧権限のある文書が見つかりませんでした。」（存在秘匿。IADR-0009） |
| 権限スコープ解決失敗 | フィルタ無し扱いを避け空スコープで継続 | （権限外文書は後段検索フィルタで除外） |
| BFF→後段が非 2xx | 後段ステータスを透過 | 後段ステータスコード |

## 受け入れ基準

- [x] AI 回答に番号付き出典が付与され、各出典が元文書リンクを持つ。
- [x] 出典番号が回答本文・LLM 文脈と一致する。
- [x] 権限の無い文書は ABAC フィルタにより検索・回答のいずれにも現れない（後段で担保）。
- [x] `/bff/analysis/ask` から単一窓口で回答＋出典を取得できる。
- [x] 応答の `Model` が実際に使用したモデルと一致する。LLM を呼んでいない縮退応答はモデル名を名乗らない（空）。（#403 / IADR-0111。T-10〜T-15・T-15f）
- [x] **出典に機密区分が載る**（FR-04 追加・2026-08-05／SC-01 裁定 Q10）。属性の欠落・空・未知値は安全側（`restricted`）へ縮退する。（#541。T-17〜T-21）

> 検証: `CitationMapperTests`（出典番号↔本文整合）／`RagOrchestratorScopeTests`（ABAC スコープ適用）／
> 統合 `RagOrchestratorTests` で担保。実装は `RagOrchestrator` ＋ BFF `/bff/analysis/ask`。

## 関連仕様

- 画面仕様書: `../screens/SC-08_ai-analysis-dashboard.md`（モデル・トークン数の補足表示）/ `../screens/SC-01_search-chat.md`（出典表示。機密区分チップは別 issue）
- 通信仕様書: `../api/openapi.yaml`（`/analysis/ask`, `/bff/analysis/ask`）
- データ仕様書: 検索結果は `SearchResultDto`、出典は `CitationDto`
- テスト仕様書: `../tests/FR-04_ai-answer-citations.md`
- 作業仕様書: `../../.ai-context/specs/20260627_FR-04_ai-answer-citations.md` / `../../.ai-context/specs/20260728_issue-403_degraded-answer-model.md` / `../../.ai-context/specs/20260806_issue-541_citation-confidentiality.md`
- 実装 ADR: `../../.ai-context/adr/IADR-0111_degraded-answer-model-label.md`（縮退応答の「使用モデル」ラベル）

## 未決事項

- `SourceUri` の最終 URL 形式は画面（文書詳細）確定後に再調整する。
