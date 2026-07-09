---
title: SC-02 検索結果一覧 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-02
  - UC-01
  - FR-03
  - FR-05
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-02_search-results.md"
  - "../specs/20260709_issue-128_sc02-search-results.md"
---

# テスト仕様書: 検索結果一覧（SC-02）

対象: `frontend/src/features/sc02-results/SearchResultsPage.tsx`
テスト: `frontend/src/features/sc02-results/SearchResultsPage.test.tsx`（Vitest + Testing Library）

## テスト観点と受け入れ基準の対応

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 検索・一覧・内部遷移 | FR-03, UC-01 | 送信で `POST /bff/search` を呼び、結果を一覧表示、タイトルが SC-03（`/documents/:id`）へリンク。属性・スニペットも表示 | `searches on submit and lists results linking to SC-03 document detail` |
| 2 | ディープリンク | FR-03 | `?q=` 付きで開くと自動検索し結果を表示 | `auto-searches from the ?q= deep link` |
| 3 | 存在秘匿（空） | FR-05, IADR-0009 | 結果 0 件（deny-by-default 含む）で中立メッセージ。権限外と 0 件を区別しない | `shows a neutral empty message when access-scoped results are empty` |
| 4 | 異常系 | FR-03 | 検索失敗時に `role="alert"` を表示 | `shows an alert when the search request fails` |
| 5 | 二重発火防止 | FR-03 | 送信 1 回で `/bff/search` は 1 回だけ（?q= 更新で重複実行しない・レビュー #168） | `does not double-fire the search when submitting (single trigger path)` |

## ABAC・存在秘匿の担保

- クライアントは検索リクエストに ABAC スコープを含めない（テストで `{ query, topK }` のみを検証）。権限解決はサーバ側（`/bff/search`）で行われ、権限外文書は結果に現れない。
- 空一覧の中立表示により、権限外文書の存在を UI から推測できない。

## 実行

- `npm run test -- src/features/sc02-results`（単体）/ `npm run test:coverage`（カバレッジ・ラチェット維持）。
