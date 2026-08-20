---
title: SC-01 検索／チャット質問画面 テスト仕様書
type: test-spec
status: completed
created: 2026-07-08
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-03, FR-04, FR-05, FR-08, FR-11, SC-01, SC-02, SC-03, SC-04, SC-08, UC-01]
adrs: [ADR-0031]
iadrs: [IADR-0009, IADR-0037, IADR-0126]
specs: [01_screens, 01_usecases, 20260804_issue-502_sc01-03-search-flow, IADR-0037_llm-sse-streaming, IADR-0126_sse-answer-state-and-search-url-state, SC-01_search-chat]
issues: [#502, #539]
-->

# テスト仕様書: 検索／チャット質問画面

> **［2026-08-04 / #502］新スタックでの再実装に合わせて改訂した。**
> バックエンド側（LlmGateway / AiAnalysisService / BFF）のケースは #127 で作成済みであり本書に残す。
> フロント側は再実装に伴い全面的に置き換わる。

## 起点となる計画書（トレーサビリティ）

- 機能要求: ハイブリッド横断検索 / 根拠付き AI 回答と出典 / ABAC アクセス制御 / 回答フィードバック / LLM 送信先の切替
- ユースケース: **検索・質問する**
- 画面: 検索／チャット質問画面
- 受け入れ基準の所在: Issue #502 ／ 仕様書: 利用者の主導線（検索・結果一覧・文書詳細）の新スタックでの再実装 §受け入れ基準

## ユースケースのフロー → テストの写像（**本書の核**）

計画リポジトリ 03_usecases「検索・質問する」の
基本・代替・例外フローを、画面側で観測できる形へ写像する。

| 検索・質問のフロー | 画面での現れ方 | テスト（`SearchChatPage.test.tsx` ほか） |
| --- | --- | --- |
| 基本 1. 利用者が質問またはキーワードを入力する | 入力が空／空白のみでは送信できない | `keeps submit disabled until a non-blank question is entered` |
| 基本 2. システムが認可（ABAC）で権限スコープを解決する | **クライアントはスコープを送らない**（要求本文は `{ question }` のみ） | `sends only the question (the client never sends an ABAC scope)` |
| 基本 3-4. 検索 → LLM が回答を生成する | `token` を逐次連結して表示。生成中は `role="status"` | `streams the answer tokens as they arrive and shows the sources` |
| 基本 5. 出典（Wiki／原本リンク）付きで結果を返す | 出典行を文書詳細・Wiki 閲覧への導線として描く | `renders document citations linking to SC-03` ／ `renders wiki citations linking to SC-04` ／ `does not infer wiki citations when no wiki base url is configured` |
| **代替. キーワード検索のみで結果一覧を返し、AI回答を省略する** | 「キーワード検索のみ →」が入力中の語を `?q=` に載せて検索結果一覧へ | `offers a keyword-only search link carrying the current question` |
| **例外. LLM が不調な場合は検索結果のみを返す（縮退運転）** | SSE の `error` で警告を出し、**検索結果一覧への導線**を示す | `degrades to keyword search when the answer stream reports an error event` ＋ `degrades to keyword search when the request itself fails`（**2 本に分かれている**） |
| 回答へのフィードバック | `done` 後に 👍/👎 が有効。`answerId` を添えて送信 | `sends feedback with the answer id after the stream completes` |

## フロント（Vitest + Testing Library）

| # | ケース | 期待 | 起点 |
| --- | --- | --- | --- |
| 1 | 空・空白のみの入力 | 送信ボタンが無効 | 検索・質問 基本 1 |
| 2 | 送信 | `POST /bff/analysis/ask/stream` を `{ question }` だけで呼ぶ | 検索・質問 基本 2 / ABAC アクセス制御 |
| 3 | `citations` → `token`* → `done` | 出典が先、本文が連結され、完了後に 👍/👎 が現れる | 検索・質問 基本 3〜5 / 根拠付き AI 回答 |
| 4 | 出典（文書） | `📄` ＋ タグ「組織文書」＋ `/docs/{documentId}` へのリンク | 検索・質問 基本 5 |
| 5 | 出典（Wiki） | `sourceUri` が `wikiBaseUrl` 配下なら `📖` ＋ `/wiki` へのリンク | 検索・質問 基本 5 |
| 6 | 「キーワード検索のみ →」 | `/search?q=<入力>` へのリンク | **検索・質問の代替フロー** |
| 7 | SSE の `error` イベント | `role="alert"` ＋ 検索結果一覧への導線 | **検索・質問の例外フロー（縮退運転）** |
| 8 | 通信失敗（`apiStream` が throw） | 同上 | 検索・質問の例外フロー |
| 9 | 中断（`AbortError`） | エラー表示を出さない | 画面のサーバー状態の持ち方（決定 1） |
| 10 | 👍 押下 | `POST /bff/feedback` に `answerId` ＋ `rating='up'` | —|
| 11 | フィードバック送信失敗 | 押下状態を戻す（楽観的更新の取り消し） | —|
| 12 | 連投（送信 → 送信） | 前のストリームを中断し、本文・出典・`answerId` をリセットする | 同上（決定 1） |
| 13 | ロケール `en` | 見出し・ボタンが英語で描画される | フロントエンドスタックの決定（i18n） |

### 純関数（`citations.ts`）

| # | 入力 | 期待 |
| --- | --- | --- |
| P-1 | `sourceUri` が `wikiBaseUrl` で始まる | `kind='wiki'`（`📖` / Wiki 閲覧画面） |
| P-2 | `sourceUri` が別ホスト | `kind='document'`（`📄` / 文書詳細画面） |
| P-3 | `sourceUri` が `null` | `kind='document'` |
| P-4 | `wikiBaseUrl` が未設定 | 常に `kind='document'`（Wiki 由来を推測しない） |

## バックエンド（#127 で作成済み・本 issue では変更しない）

| ID | レイヤ | 前提 | 期待結果 | 対応 |
| --- | --- | --- | --- | --- |
| T-01 | LlmGateway | 許可（public/rag-answer） | SSE デルタ＋`done(sent=true, tokens)` | —|
| T-02 | LlmGateway | 拒否（confidential・ティア C のみ） | プロバイダ未呼出・`sent=false`・理由（越境なし） | —|
| T-03 | AiAnalysis | 質問 | SSE `citations`→`token`→`done`（出典先行・`answerId` 付き） | —|
| T-04 | BFF | スコープ許可 | 集約検索結果 | —|
| T-05 | BFF | スコープ不許可 | 空（deny-by-default・存在秘匿） | —|
| T-06 | BFF | クライアントが Scope 偽装 | サーバ解決を優先し空（権限昇格防止） | —|
| T-07 | BFF | `ask/stream` ＋ Authorization | 上流 SSE 中継・Authorization 伝播 | —|
| T-08 | front | SSE parser | `event` / `data` 解析・複数 `data` 連結・非データは `null` | LLM 回答の SSE ストリーミング |

## E2E（Playwright）

| # | ケース | 期待 |
| --- | --- | --- |
| E-1 | 未認証で `/ask` | `/login` へ誘導（`?from=` 保持） |

**限界**: 認証済みの導線は E2E で実走できない。トークンは `InMemoryWebStorage` に保持され
（`foundation/auth/authConfig.ts`）、外部から注入できないためである。認証済みの導線は
**導線テスト**（`searchFlow.test.tsx`。3 ルートを 1 本のルータへ載せる）が担う。

## 対象範囲フィルタ（#539 / 裁定 Q1・Q3・Q9）

**実装は `features/scope-filter/scopeFilter.test.ts`（7 件）と `ScopeFilter.test.tsx`（8 件）。
AI 分析ダッシュボードと共有する部品なので、テストも 1 か所に置く。**

| # | 確かめること | 実装 |
| --- | --- | --- |
| T-30 | **軸は 3 つ（タグ・部門・プロジェクト）で「フォルダ」は無い**（裁定 Q9。不採用であって保留ではない） | `has exactly the three axes the plan fixed, and no folder` |
| T-31 | 選択の切り替え（元の選択を破壊しない） | `toggles a value on and off without mutating the original selection` |
| T-32 | **同じ軸に複数の値を保つ**（チップは複数選ぶ＝多値が要る根拠） | `keeps multiple values on the same axis` |
| T-33 | 軸をまたいで契約の形へ変換する | `combines axes into the contract shape` |
| T-34 | **空の軸は載せない**（「絞ったのに効いていない」と読ませない） | `omits axes with no selection` |
| T-35 | **何も選ばなければ `undefined`**（旧クライアントと同じ要求の形） | `returns undefined when nothing is selected` |
| T-36 | 選択件数を数える | `counts the selected values across axes` |
| T-37 | ★ **候補 API の値でチップを組み立てる**（権限内に限る） | `renders chips from the permitted candidate values` |
| T-38 | **3 軸すべてを引く**（引き漏らすとその軸だけ黙って絞れない） | `queries all three axes` |
| T-39 | チップ押下で選択を通知する | `reports the selection when a chip is pressed` |
| T-40 | ★ **選択を色だけで表さない**（`aria-pressed` ＋ ✓。INDEX 決定 21） | `marks the selected chip without relying on colour alone` |
| T-41 | 絞り込み件数／すべてが対象を文字で示す | `shows how many values are narrowing the scope` / `says everything is in scope when nothing is selected` |
| T-42 | **候補が無いときは中立文言**（「権限が無い」と区別しない。存在秘匿の方針） | `uses a neutral message when there are no candidates at all` |
| T-43 | **1 軸の失敗で他の軸の候補まで失わない** | `degrades one failing axis without losing the others` |
| T-44 | ★ 本画面が選択を**質問と一緒に送る**（SSE の要求本文） | `SearchChatPage.test.tsx` の `sends the selected scope with the question` |

## 未決事項

- なし（画面要素の不足は 仕様書: 利用者の主導線（検索・結果一覧・文書詳細）の新スタックでの再実装 と
  `feedback/20260804_sc01-03-bff-contract-gaps.md` に集約した。
  **対象範囲フィルタは #539 で実装した**）

<!-- trace-table:
row1: FR-08
row2: FR-08
row3: FR-04
row4: FR-11
row5: FR-04
row6: FR-03
row7: FR-05
row8: FR-05
row9: FR-04
-->
