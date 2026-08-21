---
title: IADR-0037 LLM 回答の SSE ストリーミング（egress ゲート保持）
type: impl-adr
status: Accepted
related_ids:
  - SC-01
  - FR-04
  - FR-11
  - UC-01
  - ADR-0010
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# IADR-0037: LLM 回答の SSE ストリーミング（egress ゲート保持）

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（実装）／ ステークホルダー判断（SC-01 は真のストリーミングで実装する）

## 起点・関連

- 関連する計画書 ID: SC-01（検索/チャット・ストリーミング表示）／ FR-04（RAG 回答）／ FR-11（LLM 越境制御）／ UC-01
- 関連 ADR: ADR-0010（LLM 抽象化）／ [IADR-0007](./IADR-0007_llm-egress-routing-config-driven.md)（LLM egress 経路のルーティング）／ [IADR-0022](./IADR-0022_default-opus-and-fable5-copilot-routes.md)（既定 Opus）／ [IADR-0033](./IADR-0033_frontend-spa-foundation.md)（SPA 基盤）
- 関連する実装仕様書: `docs/specs/20260708_issue-127_sc01-search-chat.md`

## コンテキストと課題

SC-01 は「回答をストリーミング表示」する。現状の LLM 経路（SPA ← BFF ← AiAnalysisService ← LlmGateway ← プロバイダ）は
すべて非ストリーミング（単一 JSON）である。SC-01 では**フロントエンドを真の SSE ストリーミングで実装**する方針が
確定した。課題は、**FR-11 の LLM 越境制御（egress ゲート）を一切弱めずに**ストリーミングを追加すること。

## 決定

1. **egress ゲートの保持（最重要）**: ストリーミング経路もルーティング判定 `ILlmRouter.Route(...)` を**同一に通す**。
   `Allowed=false`（機密区分により送信不可）の場合は**プロバイダを一切呼ばず**、SSE で拒否理由のみを流して終了する
   （既存 `Sent=false` 縮退と同義）。ストリーミングは送信可否の判定後にのみプロバイダを呼ぶため、越境保証は
   非ストリーミング経路と同一（[IADR-0007](./IADR-0007_llm-egress-routing-config-driven.md) を弱めない）。

2. **プロバイダ層**: `ILlmProvider` に `IAsyncEnumerable<CompletionChunk> StreamAsync(CompletionRequest, ct)` を追加する。
   **既定実装（default interface method）は `CompleteAsync` を呼んで結果を 1 チャンクで返す**（未対応プロバイダは
   単一チャンクへ縮退＝壊れない）。`ClaudeProvider` は Anthropic SDK の `StreamClaudeMessageAsync` で**真のトークン
   ストリーミング**を実装する（既定・主経路）。SelfHosted/Copilot は既定縮退のまま（後続で個別対応可）。

3. **LlmGateway**: `POST /complete/stream`（SSE, `text/event-stream`）を追加する。ルーティング後、`data:` 行で
   本文デルタを流し、最終イベントでモデル・トークン数を返す。拒否時は理由イベントのみ。既存 `POST /complete` は残す
   （非ストリーミングの利用者・後方互換のため。「バックエンド機能として両対応」）。

4. **AiAnalysisService**: `IRagOrchestrator.AskStreamAsync` を追加し、`POST /analysis/ask/stream`（SSE）を公開する。
   フロー: ABAC スコープ解決 →（不許可なら空回答イベント）→ 検索 → **出典(citations)イベントを先に送出** →
   LLM デルタを token イベントで送出 → done イベント（answerId・model・tokens）。出典は LLM 生成前に確定するため、
   フロントは本文ストリーム中に出典を先行表示できる。

5. **BFF**: `POST /bff/analysis/ask/stream` を SSE パススルーで追加（Authorization 伝播）。あわせて `GET/POST /bff/search`
   のスタブを実装し、ABAC スコープ解決（AuthorizationService）→ RetrievalService `/search` 集約で横断検索を実データ化する。

6. **フロントエンド**: `fetch` + `ReadableStream` で SSE を購読する（`EventSource` は Authorization ヘッダを付与できない
   ため不採用）。token を逐次連結して表示し、citations を併記、done で確定して 👍/👎 を有効化する。

## 検討した選択肢

- A. 上記（プロバイダ層に真の streaming、egress ゲート保持、既定縮退）— 採用。
- B. サーバ側で完成回答を分割して SSE で擬似ストリーミング — 「真のストリーミング」要件を満たさない。却下。
- C. 全プロバイダに即時 streaming 実装 — 変更面が広く外部 API 依存が増える。既定縮退で段階導入する（本 ADR）。

## 結果

- 良い影響: SC-01 が真の SSE ストリーミングで動作。egress 保証は不変。非ストリーミング `/complete`・`/analysis/ask` も存置。
- 悪い影響・トレードオフ: SelfHosted/Copilot は当面単一チャンク縮退。ストリーミング時のトークン計上はベストエフォート。
- フォローアップ: SelfHosted（OpenAI 互換 SSE）・Copilot の streaming 対応は後続。OpenAPI に stream 経路を追記。

## 関連

- Supersedes: なし ／ Superseded by: なし
