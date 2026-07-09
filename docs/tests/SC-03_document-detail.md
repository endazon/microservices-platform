---
title: SC-03 文書詳細／プレビュー テスト仕様書
type: test-spec
status: draft
related_ids:
  - SC-03
  - UC-01
  - UC-07
  - FR-06
  - FR-12
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-03_document-detail.md"
  - "../specs/20260709_issue-129_sc03-document-detail.md"
  - "../adr/IADR-0038_bff-document-read-abac-gating.md"
---

# テスト仕様書: SC-03 文書詳細／プレビュー

> 計画の受け入れ基準（Issue #129）と UC-01/UC-07 のフローをテストケースへ写像する。

## 起点となる計画書（トレーサビリティ）

- 画面: SC-03（文書詳細／プレビュー）／ UC-01・UC-07 ／ FR-06・FR-12・FR-05

## 受け入れ基準 → テストの対応

| 受け入れ基準（#129） | テスト |
| --- | --- |
| 正規化文書（Markdown）が表示され、出典元リンクが機能する | FE: `renders metadata, markdown body, source link and version history` / `shows the SC-04 Wiki navigation link` |
| 権限外の情報が表示されない（ABAC・存在秘匿） | BFF: `GetDetail_WhenScopeNotGranted_Returns404` / `GetDetail_WhenAttributesOutOfScope_Returns404` / `GetList_ReturnsOnlyInScopeDocuments` ／ FE: `shows a neutral message on 404` |
| 画面仕様書の作成・整合 | `docs/screens/SC-03_document-detail.md` |
| テスト観点の展開 | 本書 |

## BFF（xUnit）: `BffDocumentEndpointTests`

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 許可 & 属性がスコープ内 → 詳細取得 | 200・`DocumentDto` |
| 2 | スコープ非許可（deny-by-default） → 詳細 | 404（存在秘匿） |
| 3 | 許可だが属性がフィルタ外 | 404（存在秘匿） |
| 4 | DocumentService が 404（不在） | 404（不在と拒否を区別しない） |
| 5 | 一覧: internal のみ許可 | 権限内 1 件のみ、secret 文書は非列挙 |
| 6 | 一覧: 非許可 | 空配列 |
| 7 | 本文: 許可（ストレージ未配備） | 200・プレースホルダ本文＋`sourceUri` |
| 8 | 本文: 非許可 | 404 |
| 9 | 版履歴: 許可 | 200・版一覧（新しい順） |
| 10 | 版履歴: 非許可 | 404 |

## フロント（Vitest + Testing Library）: `DocumentDetailPage.test.tsx`

| # | ケース | 期待 |
| --- | --- | --- |
| 1 | 正常 | メタ・本文（Markdown 原文）・版履歴を描画 |
| 2 | Wiki 導線 | `wikiBaseUrl` 設定時に `/wiki` リンク |
| 3 | 404 | 中立「文書が見つかりませんでした。」（存在秘匿） |
| 4 | 5xx | `role="alert"` 取得に失敗 |
| 5 | 本文取得失敗 | 詳細は表示、本文領域のみ「本文は利用できません。」へ縮退 |

## 手動確認（任意）

- 実 MinIO 配備時に `storage://` から実本文が取得されること（未配備時はプレースホルダ）。
- 検索結果一覧（SC-02・#128 実装後）から遷移して同一文書が開けること。
