---
title: thinking（拡張思考）content ブロックで LLM 応答全体を失う欠陥を是正する（ai-stock-trading#290 の調査から）
type: spec
status: done
related_ids:
  - FR-04
  - FR-11
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0104
  - IADR-0112
  - IADR-0113
  - IADR-0114
author: claude
created: 2026-08-01
updated: 2026-08-01
related_specs:
  - "../adr/IADR-0114_anthropic-unknown-content-block-sanitizing.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md"
  - "../adr/IADR-0113_report-monthly-zdr-model.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
---

# 仕様書: thinking content ブロックで LLM 応答全体を失う欠陥の是正

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [ai-stock-trading#290](https://github.com/endazon/ai-stock-trading/issues/290)
  「LLM 構造化出力の解析が断続的に失敗し Hold に倒れる」。同 issue の調査中に、**別系統かつより重篤な**
  本欠陥（本リポジトリ側）を実測で確定させたため、本 PR はそちらを是正する。#290 が観測している
  rationale は本欠陥では出ない（後述「#290 との関係」）ため、issue は閉じない（`Refs`）。
- 直前の実装判断: [[IADR-0113]]（月報の ZDR 対応モデルへの改定）。同 IADR は残作業として
  **「live 実原因の確定（稼働環境不触のため未確認）」**を挙げ、live の `Sent=false` を
  「プロバイダ未登録／**呼び出し例外**」の別分岐と推定していた。本仕様書はその**呼び出し例外の正体**を確定させる。
- 計画根拠: ADR-0010（LLM ゲートウェイ・本文凍結）／ADR-0025（グローバル既定 Opus 5・Accepted）。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）／FR-04（AI 応答生成）。
- 本作業の実装判断は [[IADR-0114]]。

## 背景と問題（原因の確定）

### 事実 1: Anthropic.SDK 4.0.0 が知る content ブロック型は 4 種だけ

`ClaudeProvider.CompleteAsync` は `Anthropic.SDK` 4.0.0 の `GetClaudeMessageAsync` を使う。同 SDK の
`ContentConverter`（`ContentBase` の多態デシリアライズ）は判別子を**列挙で分岐**し、未知の型で
`JsonException` を投げる。本 PR の worktree で実測した結果は次のとおり（`AnthropicClient` へ
スタブ `HttpClient` を渡して応答本文を固定）。

| `content[].type` | SDK 4.0.0 |
| --- | --- |
| `text` / `image` / `tool_use` / `tool_result` | 解析できる |
| `thinking` | **`JsonException: Unknown type thinking`** |
| `redacted_thinking` | `JsonException: Unknown type redacted_thinking` |
| `server_tool_use` | `JsonException: Unknown type server_tool_use` |

未知型が **1 個でも**混ざると配列全体＝**応答全体**の解析に失敗する（部分的な劣化ではない）。

### 事実 2: 現在の割当モデルはすべて thinking が既定で有効

[[IADR-0112]] / [[IADR-0113]] 以降の `PurposeModels` は次のとおりで、`claude-opus-5` /
`claude-sonnet-5` は **thinking（拡張思考）が既定で有効**、`claude-fable-5` は **常時有効で無効化不可**
（無効化指定は 400）である。[[IADR-0101]] の注記どおり `MaxTokens` を思考込みで見積もる対処は入って
いるが、**応答の content に thinking ブロックが載ること自体**への対処は無かった。

| purpose | モデル | thinking |
| --- | --- | --- |
| `trade-decision` / `report-daily` / `rag-answer` | `claude-sonnet-5` | 既定で有効 |
| `report-weekly` / `report-monthly` / `default` | `claude-opus-5` | 既定で有効 |
| `analysis` | `claude-fable-5` | 常時有効（無効化不可） |

したがって非ストリーミング `/complete` は**断続ではなく決定的に全件失敗**する。
`CompletionEndpoints` は例外を握って 500 を返さない設計のため、症状は
`Sent=false`＋「呼び出し先 claude-managed が現在利用できません。」という縮退応答になる。

### 事実 3: ストリーミング（`/complete/stream`）は壊れていない

同じ実測で、SSE 経路（`StreamClaudeMessageAsync`）に `content_block_start`(thinking)・
`thinking_delta`・`signature_delta` を流しても**例外は発生せず**、本文の `text_delta` は正常に
取り出せた。SSE 経路は content の多態デシリアライズを通らないためである。よって**是正対象は
非ストリーミング経路のみ**であり、SSE はサニタイズ対象から外す（ストリームの全量バッファリングを
持ち込まないためでもある）。

### #290 との関係（issue の前提の訂正）

本欠陥が起きたとき AST 側（`HttpLlmCompletionClient`）は `Sent=false` 分岐へ入り、rationale は
`LLM ゲートウェイ送信不可のため見送り` になる。#290 が報告する `解析不能または見送り` は
`TradeDecisionParser` / `DecisionAggregator` 由来の別文言であり、**本欠陥からは出ない**。
すなわち #290 の 8 分岐のうち本欠陥に起因するものは無い。両者は独立した障害で、本欠陥のほうが
重篤（実 LLM 経路が全件不成立）である。#290 の可観測性・堅牢化は AST 側で別途消化する。

## 対象範囲

### 変更する

1. `AnthropicContentBlockSanitizer`（新規・純関数）: Messages API の JSON 応答から、
   **既知型の許可リスト**（`text` / `image` / `tool_use` / `tool_result`）に無い content ブロックを
   取り除く。既知型だけの応答は**書き換えない**（本文に触れない）。
2. `AnthropicResponseSanitizingHandler`（新規・`DelegatingHandler`）: 2xx かつ `application/json` の
   応答にのみ 1 を適用し、除去した型名を WARN で記録する。
3. `Program.cs`: `AnthropicClient` を `APIAuthentication` + サニタイズ済み `HttpClient` で構成する。

### 変更しない（意図的に対象外）

- **`Anthropic.SDK` のバージョン**（4.0.0 のまま）。上げても未知型で fail-closed する構造は変わらない
  （4.0.0 が `server_tool_use` ですら落ちるのが実証）。詳細は [[IADR-0114]] §検討した選択肢。
- **リクエスト側のパラメータ**（`thinking` / `effort` / `temperature` 等）。`ClaudeProvider` の
  「Opus 5 で 400 になるパラメータを持ち込まない」方針（[[IADR-0101]]）は不変。
- **ストリーミング経路**（事実 3 のとおり壊れていない）。
- **割当モデル・ルーティング・機密区分**（[[IADR-0112]] / [[IADR-0113]] の決定は不変）。
- **[[IADR-0104]] の安全既定**（`refusal` は本文を返さない）。thinking の有無に関わらず維持する。
- AST 側の実装（#290 の可観測性・ログ文言〔AST#315〕は別 issue）。

## 受け入れ基準

- [x] thinking ブロックを含む応答で `CompleteAsync` が**例外を投げない**。
- [x] 同応答から**本文テキスト**と `stopReason` / トークン数を従来どおり取得できる。
- [x] 取引判断の構造化出力（JSON 本文）が原文のまま届き、`action` が Buy / Sell / Hold として読める。
- [x] `redacted_thinking` / `server_tool_use` / **未知の将来型**でも同じ経路で救われる（型名の列挙に依存しない）。
- [x] `text` / `image` / `tool_use` / `tool_result` は落とさない。既知型だけの応答は書き換えない。
- [x] 全ブロックが未知でも例外にせず content 空へ縮退する（呼び出し側は空応答として安全側に扱う）。
- [x] 非 2xx・非 JSON（SSE）応答は素通しする。
- [x] 除去したブロック型を WARN ログに残す（型名のみ。本文は載せない）。
- [x] `refusal` の本文破棄（[[IADR-0104]]）が維持される。

## 実装方針（TDD）

1. 実測プローブで SDK の挙動（許可リストと例外文言）を確定 → 破棄。
2. 回帰テストを先に書き、コンパイル不成立（red）を確認。
3. 純関数 → ハンドラ → DI 配線の順に実装し green にする。
4. **変異テスト**として「サニタイズを外すと同じ応答で `Unknown type thinking` になる」を常設し、
   ガードが load-bearing であることを恒久的に証明する。

## テスト観点

`docs/tests/FR-11_llm-egress-routing.md` の **T-24** として記載。19 ケース
（`AnthropicContentBlockSanitizerTests` 11 / `ClaudeProviderThinkingTests` 8）。

## 完了条件（DoD）

- `dotnet build` / `dotnet test`（LlmGateway: 145 件）green・`dotnet format` 適用済み。
- 仕様書・IADR-0114・ADR 索引・テスト仕様（T-24）を更新済み。
- 稼働中環境への操作なし（設定・割当・実弾に関わる既定は不変）。

## 残（本 PR スコープ外）

- #290 本体（AST 側パーサ／集約層の可観測性 8 分岐の切り分けと堅牢化）。
- AST の縮退ログ文言の是正（[ai-stock-trading#315](https://github.com/endazon/ai-stock-trading/issues/315)）。
  本欠陥は「機密区分による縮退」ではないのに同一文言が出る。
- thinking 本文（`display: "summarized"`）を**活用**したい場合の SDK 更新検討（別 PR）。
- live 環境での実地確認（稼働環境不触のため本 PR では行わない）。
