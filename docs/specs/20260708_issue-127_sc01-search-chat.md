---
title: SC-01 検索／チャット質問画面（Issue #127）
type: spec
status: draft
related_ids:
  - SC-01
  - UC-01
  - UC-02
  - FR-03
  - FR-04
  - FR-11
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# 仕様書: SC-01 検索／チャット質問画面（Issue #127）

> Wave A 4 件目。全スタック（backend SSE + BFF 検索集約 + SPA 真の SSE）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03（検索）・FR-04（RAG 回答）・FR-11（LLM 越境）・FR-08（フィードバック）
- ユースケース（UC）: UC-01（横断検索・AI 質問）
- 画面（SC）: SC-01 検索／チャット質問画面
- 関連 ADR: ADR-0010 ／ [[IADR-0007]]（egress）／ **[[IADR-0037]]（SSE ストリーミング・新規）** ／ [[IADR-0033]]
- Issue: #127（親 #121）

## 目的・背景

本システムの主入口。横断検索と根拠付き AI 回答（**真の SSE ストリーミング**表示・出典併記）＋👍/👎、出典クリックで
SC-03/SC-04 へ遷移。ユーザー判断により、backend は SSE ストリーミングに対応し（[[IADR-0037]]）、frontend は SSE で
真のストリーミングを実装する。`/bff/search` のスタブも実装して横断検索を実データ化する。

## 対象範囲

- 対象（backend, [[IADR-0037]]）:
  - `ILlmProvider.StreamAsync`（既定縮退＋ ClaudeProvider 真ストリーミング）
  - LlmGateway `POST /complete/stream`（SSE、egress ゲート保持）
  - AiAnalysisService `IRagOrchestrator.AskStreamAsync` ＋ `POST /analysis/ask/stream`（SSE）
  - BFF `POST /bff/analysis/ask/stream`（SSE パススルー）／ `POST /bff/search`（ABAC スコープ解決 → RetrievalService 集約）
- 対象（frontend）:
  - `features/sc01-search`（`/`? いや home は既存。ルート `/search`）: 検索フォーム＋結果一覧、チャット（SSE 逐次表示・出典併記）、👍/👎、出典→SC-03/SC-04 導線。
  - foundation に SSE 購読ヘルパ（`apiStream`, fetch + ReadableStream, Bearer 付与）。
- 対象外:
  - SelfHosted/Copilot の streaming（既定縮退）。SC-03/SC-04 本体（導線のみ・後続 #129/#130）。

## 設計（要点）

- **egress 保持**: `/complete/stream` はルーティング判定を非ストリーミングと同一に通し、`Allowed=false` はプロバイダ未呼出で理由のみ SSE。
- **SSE プロトコル（/analysis/ask/stream, /bff/analysis/ask/stream）**: `event: citations`（出典配列）→ `event: token`（デルタ）* → `event: done`（answerId/model/tokens）／ `event: error`。
- **検索**: `/bff/search` は AuthorizationService `/authz/scope` でスコープ解決（deny-by-default）→ RetrievalService `/search` を scope 付きで呼ぶ。権限外は空（存在秘匿）。
- **フロント**: `fetch`+`ReadableStream` で SSE 購読（EventSource は Bearer 付与不可）。token 連結表示、citations 併記、done で 👍/👎 有効化。

## 受け入れ基準（Issue #127）

- [ ] 画面仕様書が作成され、計画の画面設計・対応 UC と整合している
- [ ] 質問送信で回答がストリーミング表示され、出典が併記される
- [ ] 出典クリックで文書詳細（SC-03）／Wiki（SC-04）へ遷移できる（導線）
- [ ] 権限外の情報が表示されない（ABAC・存在秘匿）
- [ ] テスト観点が `docs/tests/` へ展開されている

## テスト方針

- backend（xUnit）: `/complete/stream` の egress 拒否（プロバイダ未呼出）・許可時のデルタ、`AskStreamAsync` の citations→token→done、`/bff/search` のスコープ解決・空縮退。
- frontend（Vitest）: SSE 購読ヘルパのパース、SC-01 の逐次表示・出典・👍/👎・検索結果・空縮退。E2E: 未認証 `/search`→`/login`。

## 計画書との差異

- 差異: あり（記録）。SC-01 の「ストリーミング」を真の SSE で実装するため backend を拡張（[[IADR-0037]]）。egress（FR-11）は不変。

## 未決事項

- 出典リンク先の内部遷移は SC-03（#129）実装後に接続（当面 sourceUri 直リンク／documentId 保持）。
