---
title: SC-01 検索／チャット質問画面 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-01
  - UC-01
  - FR-03
  - FR-04
  - FR-05
  - FR-08
  - FR-11
  - IADR-0126
author: claude
created: 2026-07-08
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-01_search-chat.md"
  - "../specs/20260804_issue-502_sc01-03-search-flow.md"
  - "../adr/IADR-0037_llm-sse-streaming.md"
  - "../adr/IADR-0126_sse-answer-state-and-search-url-state.md"
---

# テスト仕様書: SC-01 検索／チャット質問画面

> **［2026-08-04 / #502］新スタックでの再実装に合わせて改訂した。**
> バックエンド側（LlmGateway / AiAnalysisService / BFF）のケースは #127 で作成済みであり本書に残す。
> フロント側は再実装に伴い全面的に置き換わる。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-03 / FR-04 / FR-05 / FR-08 / FR-11
- ユースケース（UC）: **UC-01**（検索・質問する）
- 画面（SC）: SC-01
- 受け入れ基準の所在: Issue #502 ／ [作業仕様書](../specs/20260804_issue-502_sc01-03-search-flow.md) §受け入れ基準

## UC-01 のフロー → テストの写像（**本書の核**）

[03_usecases UC-01](../../planning/projects/microservices-platform/03_usecases/01_usecases.md) の
基本・代替・例外フローを、画面側で観測できる形へ写像する。

| UC-01 のフロー | 画面での現れ方 | テスト（`SearchChatPage.test.tsx` ほか） |
| --- | --- | --- |
| 基本 1. 利用者が質問またはキーワードを入力する | 入力が空／空白のみでは送信できない | `submit stays disabled until a non-blank question is entered` |
| 基本 2. システムが認可（ABAC）で権限スコープを解決する | **クライアントはスコープを送らない**（要求本文は `{ question }` のみ） | `sends only the question (the client never sends an ABAC scope)` |
| 基本 3-4. 検索 → LLM が回答を生成する | `token` を逐次連結して表示。生成中は `role="status"` | `streams the answer tokens as they arrive` |
| 基本 5. 出典（Wiki／原本リンク）付きで結果を返す | 出典行を SC-03 / SC-04 への導線として描く | `renders document citations linking to SC-03` / `renders wiki citations linking to SC-04` |
| **代替. キーワード検索のみで結果一覧を返し、AI回答を省略する** | 「キーワード検索のみ →」が入力中の語を `?q=` に載せて SC-02 へ | `offers a keyword-only search link carrying the current question` |
| **例外. LLM が不調な場合は検索結果のみを返す（縮退運転）** | SSE の `error` で警告を出し、**検索結果一覧への導線**を示す | `degrades to keyword search when the answer stream fails` |
| （FR-08）回答へのフィードバック | `done` 後に 👍/👎 が有効。`answerId` を添えて送信 | `sends feedback with the answer id after the stream completes` |

## フロント（Vitest + Testing Library）

| # | ケース | 期待 | 起点 |
| --- | --- | --- | --- |
| 1 | 空・空白のみの入力 | 送信ボタンが無効 | UC-01 基本 1 |
| 2 | 送信 | `POST /bff/analysis/ask/stream` を `{ question }` だけで呼ぶ | UC-01 基本 2 / FR-05 |
| 3 | `citations` → `token`* → `done` | 出典が先、本文が連結され、完了後に 👍/👎 が現れる | UC-01 基本 3-5 / FR-04 |
| 4 | 出典（文書） | `📄` ＋ タグ「組織文書」＋ `/docs/{documentId}` へのリンク | UC-01 基本 5 |
| 5 | 出典（Wiki） | `sourceUri` が `wikiBaseUrl` 配下なら `📖` ＋ `/wiki` へのリンク | UC-01 基本 5 |
| 6 | 「キーワード検索のみ →」 | `/search?q=<入力>` へのリンク | **UC-01 代替フロー** |
| 7 | SSE の `error` イベント | `role="alert"` ＋ 検索結果一覧への導線 | **UC-01 例外フロー（縮退運転）** |
| 8 | 通信失敗（`apiStream` が throw） | 同上 | UC-01 例外フロー |
| 9 | 中断（`AbortError`） | エラー表示を出さない | [[IADR-0126]] 決定 1 |
| 10 | 👍 押下 | `POST /bff/feedback` に `answerId` ＋ `rating='up'` | FR-08 |
| 11 | フィードバック送信失敗 | 押下状態を戻す（楽観的更新の取り消し） | FR-08 |
| 12 | 連投（送信 → 送信） | 前のストリームを中断し、本文・出典・`answerId` をリセットする | [[IADR-0126]] 決定 1 |
| 13 | ロケール `en` | 見出し・ボタンが英語で描画される | ADR-0031（i18n） |

### 純関数（`citations.ts`）

| # | 入力 | 期待 |
| --- | --- | --- |
| P-1 | `sourceUri` が `wikiBaseUrl` で始まる | `kind='wiki'`（`📖` / SC-04） |
| P-2 | `sourceUri` が別ホスト | `kind='document'`（`📄` / SC-03） |
| P-3 | `sourceUri` が `null` | `kind='document'` |
| P-4 | `wikiBaseUrl` が未設定 | 常に `kind='document'`（Wiki 由来を推測しない） |

## バックエンド（#127 で作成済み・本 issue では変更しない）

| ID | レイヤ | 前提 | 期待結果 | 対応 |
| --- | --- | --- | --- | --- |
| T-01 | LlmGateway | 許可（public/rag-answer） | SSE デルタ＋`done(sent=true, tokens)` | FR-04 |
| T-02 | LlmGateway | 拒否（confidential・ティア C のみ） | プロバイダ未呼出・`sent=false`・理由（越境なし） | FR-11 |
| T-03 | AiAnalysis | 質問 | SSE `citations`→`token`→`done`（出典先行・`answerId` 付き） | FR-04 |
| T-04 | BFF | スコープ許可 | 集約検索結果 | FR-03 |
| T-05 | BFF | スコープ不許可 | 空（deny-by-default・存在秘匿） | FR-05 |
| T-06 | BFF | クライアントが Scope 偽装 | サーバ解決を優先し空（権限昇格防止） | FR-05 |
| T-07 | BFF | `ask/stream` ＋ Authorization | 上流 SSE 中継・Authorization 伝播 | FR-04 |
| T-08 | front | SSE parser | `event` / `data` 解析・複数 `data` 連結・非データは `null` | [[IADR-0037]] |

## E2E（Playwright）

| # | ケース | 期待 |
| --- | --- | --- |
| E-1 | 未認証で `/ask` | `/login` へ誘導（`?from=` 保持） |

**限界**: 認証済みの導線は E2E で実走できない。トークンは `InMemoryWebStorage` に保持され
（`foundation/auth/authConfig.ts`）、外部から注入できないためである。認証済みの導線は
**導線テスト**（`searchFlow.test.tsx`。3 ルートを 1 本のルータへ載せる）が担う。

## 未決事項

- なし（画面要素の不足は [作業仕様書 §2](../specs/20260804_issue-502_sc01-03-search-flow.md) と
  `feedback/20260804_sc01-03-bff-contract-gaps.md` に集約した）
