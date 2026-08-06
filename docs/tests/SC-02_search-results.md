---
title: SC-02 検索結果一覧 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-02
  - UC-01
  - FR-03
  - FR-05
  - IADR-0126
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-02_search-results.md"
  - "../specs/20260804_issue-502_sc01-03-search-flow.md"
  - "../adr/IADR-0126_sse-answer-state-and-search-url-state.md"
---

# テスト仕様書: 検索結果一覧（SC-02）

> **［2026-08-04 / #502］新スタックでの再実装に合わせて改訂した。**

対象: `src/knowledge/frontend/src/features/sc02-results/`
テスト: `SearchResultsPage.test.tsx`（Vitest + Testing Library）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-02 ／ ユースケース（UC）: **UC-01**（**代替フロー**の受け皿）／ 機能要求（FR）: FR-03・FR-05

## UC-01 のフロー → テストの写像

| UC-01 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **代替. キーワード検索のみで結果一覧を返し、AI回答を省略する** | 本画面は AI 回答を一切呼ばない。`POST /bff/search` だけを呼ぶ | `searches via /bff/search and lists results linking to SC-03` |
| 基本 2. システムが認可（ABAC）で権限スコープを解決する | **クライアントはスコープを送らない**（要求は `{ query, topK }` のみ） | 同上（要求本文の検証） |
| 基本 3. 属性フィルタ付きハイブリッド検索 | 件数に「（権限内のみ表示）」を添える | `states that only permitted documents are listed` |
| 例外（FR-05・存在秘匿）. 権限外は結果に現れない | 0 件と権限外を**同じ中立文言**で示す | `shows a neutral empty message when results are empty (existence hidden)` |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 検索・一覧・内部遷移 | FR-03 / UC-01 | 送信で `POST /bff/search` を `{ query, topK: 20 }` で呼び、結果を表示。タイトルが `/docs/{documentId}` へリンク。スニペットとタグも出る |
| 2 | ディープリンク | FR-03 | `?q=` 付きで開くと自動で検索して結果を表示する |
| 3 | 存在秘匿（空） | FR-05 / [[IADR-0009]] | 0 件（deny-by-default 含む）で中立メッセージ。権限外と 0 件を区別しない |
| 4 | 件数表示 | FR-05 | 「N 件（権限内のみ表示）」。総数 > 表示件数のときは表示件数も示す |
| 5 | 異常系 | FR-03 | 検索失敗時に `role="alert"` |
| 6 | **単一発火** | [[IADR-0126]] 決定 3 | 送信 1 回で `/bff/search` への要求は **1 回だけ**。URL が単一情報源であり、入力欄は取得の引き金にならない（**モックの当て先＝実装の詳細に依らない書き方にしてある**。#519 で `apiFetch` → `apiRequest` へ移り、当て先を書いた記述が腐った） |
| 7 | 空クエリ | — | `?q=` が空なら**要求を出さない**（`enabled: false`） |
| 8 | SC-01 への復帰 | 導線 | 「← チャットに戻る」が入力中の語を保って `/ask` へ |
| 8-b | **`?q=` の変化への追随** | [[IADR-0126]] 決定 3（2026-08-05 追記） | **アンマウントを伴わずに** `?q=` だけが変わる経路（`router.navigate` ＋ `router.history.back()`＝ブラウザの戻る／進む）で、**結果一覧と入力欄の両方**が新しい語になる |
| 8-c | 編集途中の値の破棄 | 同上 | 入力途中に `?q=` が外から変わったら、未確定の編集値を捨てて URL の値にする |
| 9 | ロケール `en` | ADR-0031 | 見出し・ボタンが英語で描画される |

## ABAC・存在秘匿の担保

- クライアントは検索リクエストに ABAC スコープを含めない（テストで `{ query, topK }` のみを検証）。
  権限解決はサーバ側（`/bff/search`）で行われ、権限外文書は結果に現れない。
- 空一覧の中立表示により、権限外文書の存在を UI から推測できない。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc02-results`（単体。**11 ケース**）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
