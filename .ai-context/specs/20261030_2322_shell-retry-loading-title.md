---
title: 作業仕様書 — 共通シェルの再試行の連打・待ち表示・画面ごとの document.title（#2322）
type: spec
status: done
related_ids: [SC-01, SC-17, NFR, ADR-0031, IADR-0009, IADR-0124, IADR-0125]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2322"
---

# 作業仕様書 — 共通シェルの細部（#2322）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `093bc95b`）。親 #2146（項目 1）。

## 現状（develop で確認）

| 項目 | 現状 | 扱い |
| --- | --- | --- |
| 再試行の連打 | `QueryState` の再試行ボタンは、再取得の最中（`isFetching`）も押せる。押すたびに `refetch` が走る | 直す |
| 待ち表示 | `RequireAuth`・`RequireRole` の待ちは `<p role="status">読み込み中…</p>` の文字だけで、ほかの画面の待ち（`LoadingState`。輪と文言）と揃っていない | 直す |
| SC-17 の待ち | **既に `QueryState`（＝`LoadingState`）で描いている**（`UserAccountManagementPage.tsx`）。監査の時点の後で直った | 変えない |
| 画面ごとの title | `index.html` の `<title>Knowledge Platform</title>` のまま。画面を移っても変わらない。ブランド表示名（計画 §共通シェル「汎用プラットフォーム」）とも違う | 直す |

## 方針

1. **再試行**: 再取得の最中（`query.isFetching`）は再試行ボタンを `disabled` にする。終われば押せる。
2. **待ち表示**: 2 つのガードの待ちを `@platform/ui` の `LoadingState` にする。文言（`読み込み中…`）と `role="status"`（`Spinner` が持つ）は変わらない。
3. **title**:
   - 共通シェル（`Layout`）が、パンくずの宣言から画面名を引き、`document.title` を「<画面名> - 汎用プラットフォーム」にする。画面名が無ければ「汎用プラットフォーム」だけにする。
   - 画面名は**宣言の名前だけ**を使う。動的な葉（SC-03 の文書タイトル）は使わない。ブラウザの履歴・タブに文書の題名を残さないためである。葉で名を持つ画面（SC-03 など）は、親の段の名前を使う。
   - **存在秘匿（IADR-0009）**: 権限外・未知のパスはパンくずが空なので、title もブランド名だけになる。画面の存在を title から推測できない。
   - `index.html` の既定 `<title>` をブランド表示名「汎用プラットフォーム」にする（計画 §共通シェル「ブランド表示名は統一」。ロケールで差し替えない）。
   - 組み立ては純関数（`documentTitle`。`breadcrumbs.ts`）に置き、`Layout` は `useEffect` で書くだけにする。

## 母集合（規則 9・10）

- **規則 9**:
  - 再試行: `grep -rn "refetch()" --include=*.tsx`（試験を除く）で走査した。利用者が押す再試行は `QueryState.tsx` の 1 か所だけである。`msg\`再試行` も同じ 1 か所である。
  - 待ち: `<p ... role="status">` に「読み込み中・取得中・検索中・処理中」を直書きしている箇所を走査した。`RequireAuth.tsx`・`RequireRole.tsx` の 2 か所だけである。
  - title: `document.title` の書き込みは 0 件だった。`<title>` は `src/platform/frontend/index.html` の 1 か所である。
- **規則 10**:
  - 既存の試験「shows a loading indicator…」（2 本）は文言と `role` を見ているだけなので、そのまま通る。輪（`svg`）の有無を新しく足す。
  - 「Knowledge Platform」を期待する試験・E2E は 0 件だった（`grep -rn "Knowledge Platform\|toHaveTitle"`）。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 再取得の最中は再試行ボタンが押せない（終われば押せる陽性対照） | `QueryState.test.tsx` の 2 本 | 赤 |
| 待ちが共通の表示（輪と文言・`role="status"`） | `RequireAuth.test.tsx`・`RequireRole.test.tsx` | 赤（輪が無い） |
| 画面ごとに title が変わる。権限外・未知のパスはブランド名だけ。葉（文書の題名）を使わない | `breadcrumbs.test.ts`（`documentTitle`）・`Layout.test.tsx` | 赤 |

## 実装後の記録

- 初期ロードの合計が +0.26 kB（648.02 → 648.28 kB）増えた。ガード 2 つが `LoadingState` を引き、`Layout` が title を書くためで、意図した増加である。`check-chunk-budget.js --update` で床を上げた。
- テスト仕様書の行は足していない。共通シェルの部品の試験を受ける仕様書が無いためで、基盤・回帰の試験として扱う（`check-test-spec-coverage` は warn）。
