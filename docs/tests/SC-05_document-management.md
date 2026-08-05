---
title: SC-05 文書管理 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-05
  - UC-03
  - FR-06
  - FR-09
  - IADR-0041
  - IADR-0127
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../screens/SC-05_document-management.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
---

# テスト仕様書: 文書管理（SC-05）

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**

対象: `src/knowledge/frontend/src/features/sc05-documents/`
テスト: `DocumentManagementPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc05-documents.smoke.spec.ts`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-05 ／ ユースケース（UC）: **UC-03**（文書を管理する）／ 機能要求（FR）: FR-06・FR-09

## UC-03 のフロー → テストの写像

| UC-03 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 管理者が文書を登録／更新し、属性・タグを設定する** | 登録は `POST /bff/documents`、更新は `PUT`（`expectedVersion` つき） | `creates a document with the required confidentiality attribute and tags` ／ `updates a document with the optimistic-lock version and the change note` |
| **例外. 必須属性が未設定の場合は保存を拒否する** | タイトルが空（空白のみを含む）では保存ボタンが無効。注記も画面に出る | `refuses to save until the required title is filled (UC-03 exception flow)` |
| 基本 2. システムが取り込みイベントを発行し、索引と Wiki へ反映する | **写像しない**（サーバ側の責務）。画面は「保存 → 取り込み・Wiki同期をトリガ」を補助文で示すだけ | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | SC-05 / FR-06 | `GET /bff/documents` を呼び、タイトル（`/docs/$id` へのリンク）・機密区分（**生値**）・版（`v{n}`）を表示する |
| 2 | 登録 | UC-03 基本 1 / FR-09 | タイトル・機密区分・タグを送る（`{ title, attributes: { confidentiality }, tags }`） |
| 3 | **必須属性** | **UC-03 例外** | 空・空白のみでは保存できず、要求も出ない。注記が画面に出ている |
| 4 | 更新（楽観ロック） | FR-06 | 現在版を `expectedVersion` として送る。**既存の属性（部門）を落とさない** |
| 4-b | **再取得** | [[IADR-0127]] 決定 5 | 保存の成功後に一覧を **1 回だけ**取り直す（`invalidateQueries` のみ。手書きの再取得を持たない） |
| 5 | **版競合（409）** | FR-06 | 「版が変わっています」と読める文言を `role="alert"` で出す |
| 6 | 状態遷移 | FR-06 / [[IADR-0041]] | 公開は未公開（`draft`/`normalized`）の行のみ・アーカイブは `archived` 以外の行のみ |
| 7 | 削除 | FR-06 | `DELETE /bff/documents/{id}` を呼び、完了を伝える |
| 8 | **存在秘匿（404）** | [[IADR-0009]] / [[IADR-0041]] | スコープ外・不在をいずれも中立に扱い、「権限がありません」を示唆しない |
| 9 | 異常系 | — | 一覧の取得失敗で `role="alert"` |
| 10 | 0 件 | — | 「文書はありません。」 |
| 11 | **権限別の出し分け** | [[IADR-0035]] / [[IADR-0009]] | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 12 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #6 | 「変換」列が無い。**先に「機密区分」「版」の列が在ることを確かめてから**無いことを見る |
| 13 | ロケール `en` | ADR-0031 | 見出し・保存ボタンが英語で描画される |

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-05 → SC-03 | 一覧のタイトルから文書詳細へ遷移し、本文が表示される（計画の遷移図 `SC05 → SC03`） |

## ABAC・存在秘匿の担保

- 読み取りは ABAC スコープ内のみ返る。書き込みは BFF が対象文書のスコープを先に確かめ、
  スコープ外・不在を**いずれも 404** で返す（[[IADR-0041]]）。画面は 404 を中立に扱い、
  「権限がありません」を示唆する文言を出さない（#8 で固定）。
- ロールを持たない利用者にはルートもナビも存在しない（#11 で固定）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc05-documents`（単体。**13 ケース**）
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
