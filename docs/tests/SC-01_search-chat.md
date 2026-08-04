---
title: SC-01 検索／チャット質問画面 テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-01
  - UC-01
  - FR-03
  - FR-04
  - FR-08
  - FR-11
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-01_search-chat.md"
  - "../specs/20260708_issue-127_sc01-search-chat.md"
  - "../adr/IADR-0037_llm-sse-streaming.md"
---

# テスト仕様書: SC-01 検索／チャット質問画面

> UC-01 の基本・例外フローと SSE ストリーミング・egress 保持をテストへ写像する（全スタック）。

## 起点となる計画書（トレーサビリティ）

> **［2026-08-04 / #490］ルートパスを計画へ是正した。** SPA のルータを TanStack Router へ差し替えるにあたり、本書内のルート表記を [05_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)「ルートパス（wireframe の URL バー準拠）」の値へ揃えた（[[IADR-0124]] 決定 6）。テスト観点そのものは変えていない。


- 機能要求（FR）: FR-03 / FR-04 / FR-08 / FR-11 / FR-05
- ユースケース（UC）: UC-01
- 受け入れ基準の所在: Issue #127 ／ `docs/specs/20260708_issue-127_sc01-search-chat.md`

## テスト対象・範囲

- backend: LlmGateway `/complete/stream`（egress 保持）、AiAnalysisService `/analysis/ask/stream`、BFF `/bff/search`・`/bff/analysis/ask/stream`。
- frontend: SSE 購読ヘルパ（`parseSseBlock`/`apiStream`）、SC-01 画面（逐次表示・出典・👍/👎・検索）。
- 対象外: Retrieval/AuthorizationService の内部実装（既存テスト）。

## テスト観点

- egress 保持（FR-11・最重要）: `/complete/stream` は送信不可でプロバイダ未呼出・理由のみ。
- SSE 順序: citations → token* → done。出典が本文より先。
- 検索（FR-03/FR-05）: スコープ許可で集約結果、deny-by-default で空、クライアント Scope 無視。
- フロント: token 連結表示、出典リンク、👍/👎（answerId 紐付け）、検索結果、error イベントで alert。

## テストケース一覧

| ID | レイヤ | 前提 | 期待結果 | 対応 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | LlmGateway | 許可（public/rag-answer） | SSE デルタ＋done(sent=true, tokens) | FR-04 | 自動 |
| T-02 | LlmGateway | 拒否（confidential・ティアCのみ） | プロバイダ未呼出・sent=false・理由（越境なし） | FR-11 | 自動 |
| T-03 | AiAnalysis | 質問 | SSE citations→token→done（出典先行、answerId 付き） | FR-04 | 自動 |
| T-04 | BFF | スコープ許可 | 集約検索結果 | FR-03 | 自動 |
| T-05 | BFF | スコープ不許可 | 空（deny-by-default・存在秘匿） | FR-05 | 自動 |
| T-06 | BFF | クライアントが Scope 偽装 | サーバ解決を優先し空（権限昇格防止） | FR-05 | 自動 |
| T-07 | BFF | ask/stream＋Authorization | 上流 SSE 中継・Authorization 伝播 | FR-04 | 自動 |
| T-08 | front | SSE parser | event/data 解析・複数 data 連結・非データは null | IADR-0037 | 自動 |
| T-09 | front | citations→token*→done | 本文連結表示・出典リンク・検索結果表示 | UC-01 | 自動 |
| T-10 | front | done 後に 👍 | `/bff/feedback` に answerId＋rating='up' 送信・送信済表示 | FR-08 | 自動 |
| T-11 | front | error イベント | `role="alert"` 回答生成失敗 | 異常系 | 自動 |
| T-12 | front | 未認証 `/ask` | `/login` へ誘導 | 認証ガード | 自動(E2E) |

## 未決事項

- なし
