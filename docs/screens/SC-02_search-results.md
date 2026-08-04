---
title: 検索結果一覧 画面仕様書
type: screen-spec
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
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-03_document-detail.md"
  - "../adr/IADR-0038_bff-document-read-abac-gating.md"
  - "../adr/IADR-0033_frontend-spa-foundation.md"
  - "../specs/20260709_issue-128_sc02-search-results.md"
  - "../tests/SC-02_search-results.md"
---

# 画面仕様書: 検索結果一覧（SC-02）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-02 検索結果一覧**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §画面一覧・遷移図 `SC01 → SC02 → SC03`）
- 関連ユースケース（UC）: **UC-01**（検索・閲覧）
- 関連機能要求（FR）: **FR-03**（ハイブリッド検索）、**FR-05**（ABAC アクセス制御）

## 画面概要・目的
> **［2026-08-04 / #490］ルートは `/search` である。** SPA のルータを TanStack Router へ差し替えるにあたり、ルートパスを [05_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)「ルートパス（wireframe の URL バー準拠）」の値へ是正した（[[IADR-0124]] 決定 6）。画面内容そのものの計画準拠は #452 が担う。


キーワード／意味（ハイブリッド）検索の結果を一覧表示し、各件から SC-03（文書詳細）へ内部遷移する画面。SC-01（検索／AI質問）が AI 回答と出典（外部 URI）を主とするのに対し、本画面は**権限内の文書一覧と文書詳細への内部導線**を担う。

- 主要利用シーン: キーワードで文書を探し、詳細（本文・属性・版）を確認する。
- アクセス: 認証済みユーザー（一般社員）。ロール限定なし（`RequireAuth` のみ）。ABAC はサーバ側（BFF）で適用。
- 遷移: 各結果 → `GET /documents/:id`（SC-03）。`?q=` パラメータでディープリンク（SC-01 等からの連携・共有・ブラウザ戻る操作）に対応する。

## データソース（BFF 境界）

| 用途 | エンドポイント | 認可 | 応答 |
| --- | --- | --- | --- |
| 検索 | `POST /bff/search` | ABAC（BFF 集約・deny-by-default で空一覧） | `SearchResponse` |

- リクエスト: `{ query: string, topK: 20 }`。**クライアントは ABAC スコープを送らない**（サーバ側で JWT から解決。権限昇格防止・IADR-0009）。
- `SearchResponse = { results: SearchResultDto[], totalHits, elapsedMs }`
- `SearchResultDto = { chunkId, documentId, documentTitle, text, score, markdownUri?, attributes{}, tags[] }`

## レイアウト / 主要素

```
┌───────────────────────────────────────────────┐
│ 検索結果一覧                                    │
│ [ キーワード・意味検索 ............ ] [検索する]│
├───────────────────────────────────────────────┤
│ N 件（表示 M 件）                               │
│ ┌─────────────────────────────────────────┐    │
│ │ 経費規程 2025 (→SC-03)      score 0.91   │    │
│ │ 第3条 出張旅費の上限を…                   │    │
│ │ [confidentiality: internal] #hr          │    │
│ └─────────────────────────────────────────┘    │
└───────────────────────────────────────────────┘
```

## 入力 / バリデーション

| 項目 | 必須 | 形式 | バリデーション |
| --- | --- | --- | --- |
| キーワード | 必須 | テキスト | 空・空白のみは検索不可（ボタン無効） |

## 状態遷移・振る舞い

- `idle`: 未検索（`?q=` 無し）。
- `loading`: 検索中（`role="status"`）。
- `ok`: 結果表示。0 件は「該当する文書が見つかりませんでした。」（deny-by-default と 0 件を区別しない＝存在秘匿）。
- `error`: 「検索に失敗しました。」（`role="alert"`）。
- 送信時に `?q=` を URL へ反映する。マウント時／`?q=` 変化時に自動検索する。

## ABAC・存在秘匿の画面適用（受け入れ基準）

- 権限内の文書のみ表示される（BFF の deny-by-default により権限外は結果に現れない）。
- 権限外・0 件は同一の中立表示（存在の有無を露出しない）。
- 文書詳細（SC-03）側でも ABAC が再適用され、権限外は 404 中立表示となる。

## 実装

- `src/knowledge/frontend/src/features/sc02-results/SearchResultsPage.tsx` / `index.tsx`
- ナビ: 「検索結果一覧」→ `/search`（認証済み全員。左ナビ「利用者」グループ）。
- テスト観点は [tests/SC-02_search-results.md](../tests/SC-02_search-results.md)。
