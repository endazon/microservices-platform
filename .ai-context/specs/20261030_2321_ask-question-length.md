---
title: 作業仕様書 — 質問の必須と最大長（1000 文字）を後段の /ask・/ask/stream でも検証する（#2321）
type: spec
status: done
related_ids: [SC-01, FR-04, UC-01, ADR-0030, IADR-0371, IADR-0393]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2321"
---

# 作業仕様書 — 後段の質問の長さの検証（#2321）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `093bc95b`）。親 #2146（項目 4）。

## 現状

- 計画 05_screens §SC-01 の入力表は、質問を「必須・1文字以上、最大長制限」とする。値は定めていない。
- 画面仕様書 `docs/screens/SC-01_search-chat.md` は最大 1000 文字を暫定値とする（§未決事項 3）。SPA は `MAX_QUESTION_LENGTH = 1000` を置く。
- 後段 AiAnalysisService の `/analysis/ask`・`/analysis/ask/stream` は質問を検証しない。空の質問も 1000 文字を超える質問も、そのまま RAG と LLM へ渡る。SPA を通らない呼び出しには上限が無い。
- 対照: `/analysis/analyze` は `AnalyzeRequestValidator`（FluentValidation）で必須と 2000 文字を強制し、400 `{ "error": "..." }` を返す。

## 方針

1. `AskRequestValidator`（`AbstractValidator<AskRequest>`）を足す。
   - 必須: `string.IsNullOrWhiteSpace` で判定する（計画の「1文字以上」。画面も前後の空白を除いて判定する）。
   - 最大長: 1000 文字。値は画面仕様書の暫定値と SPA に揃え、定数 `AskRequestValidator.MaxQuestionLength` に 1 つだけ置く。
   - 本文は `/analyze` と同じ形 `{ "error": "<文>" }` にする。文は `question is required`・`question must be 1000 characters or fewer`。
2. `/ask` と `/ask/stream` は、最初に検証する。
   - 検証は未認証の縮退より前に置く（`/analyze` と同じ順。不正な本文は認証の有無によらず 400）。認可サービスも LLM も呼ばない。
   - `/ask/stream` は SSE のヘッダを書く前に検証する。400 は JSON で返す。
3. 登録は `Program.cs` へ 1 行で明示する（アセンブリ走査は使わない。既存の作法）。
4. BFF は変えない。
   - `/bff/analysis/ask` は後段の 4xx を本文ごと返す（`BffFailureRelay`。#2280）。
   - `/bff/analysis/ask/stream` は後段の非 2xx を SSE の `error` イベントにする（既存）。LLM は呼ばれないので、本件の目的（上限なしに LLM へ届かない）は満たす。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "AskAsync(\|AskStreamAsync(\|AskRequest\b" -- src/*/backend`（試験を除く）で走査した。
  - 質問を RAG へ渡す入口は `Ask/Endpoint.cs` と `AskStream/Endpoint.cs` の 2 か所だけである。両方を直す。
  - BFF の `AnalysisRequest` は後段へ転送するだけで、RAG を呼ばない。後段で止まるので BFF には足さない（二重に持たない）。
  - FeedbackService の `MaxQuestionLength = 1000` は保存時の切り詰めで、送信を拒否しない（FR-08 の仕様）。別の目的なので共有しない。
- **規則 10**:
  - 画面仕様書 §未決事項 3 は「暫定 1000 文字」のまま変えない（計画の値が決まっていないことは変わらない）。後段も同じ値で拒むことを §最大長 の注記に 1 行足す。
  - テスト仕様書 FR-04 に試験の行を足す。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 上限を超える質問は `/ask`・`/ask/stream` とも 400 で拒まれ、LLM を呼ばない | `AskQuestionValidationTests`（端点 2 つ × 超過・空） | 赤（200 で LLM が呼ばれる） |
| 上限ちょうどは通る（陽性対照） | 同上の境界の試験 | 緑 |
| 検証器の規則と本文 | `AskRequestValidatorTests` | 新規 |
