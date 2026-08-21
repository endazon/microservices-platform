---
title: SC-05 文書管理の検証エラー詳細表示（Issue #177）
type: spec
status: completed
related_ids:
  - SC-05
  - FR-06
  - FR-09
  - UC-03
  - IADR-0040
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md (SC-05)
  - planning:projects/microservices-platform/03_usecases/01_usecases.md (UC-03)
---

# 仕様書: SC-05 文書管理の検証エラー詳細表示（Issue #177）

## 起点となる計画書（トレーサビリティ）

- 画面(SC): SC-05（文書管理）
- 機能要求(FR): FR-06（文書）／FR-09（認可・検証エラーの提示）
- ユースケース(UC): UC-03
- 関連 ADR: [IADR-0040](../adr/IADR-0040_admin-abac-bff-passthrough-and-admin-only.md)（`ApiError.details` 統一）
- Issue: #177（関連 #131 #135 / PR #170 #171）

## 目的・背景

SC-09（#135 / PR #170）で foundation の `apiFetch`/`ApiError` に 400/409 の Problem 本文から詳細メッセージを
抽出する `ApiError.details` を追加した。SC-05（#131 / PR #171）は develop 基盤上に実装したためブランチ独立を
優先し、楽観ロック競合を `ApiError.status===409` で判定する簡易対応に留めていた（詳細メッセージ非表示）。
`ApiError.details` が develop に入ったため、SC-05 の作成/更新でも検証（400）・競合（409）の**詳細メッセージ**を
画面に出せるようにし、SC-09 と UX を統一する。

## 対象範囲

- 対象（`frontend/src/features/sc05-documents/DocumentManagementPage.tsx`）:
  - `toMessages(err, fallback)` ヘルパと `Errors` リスト表示コンポーネント（SC-09 と同一 UX）を追加。
  - 作成フォーム: 例外時に `ApiError.details`（400 検証詳細）を一覧表示。詳細が無ければ既定文言。
  - `reportError`（編集/公開/アーカイブ/削除の共通ハンドラ）: 409 競合は詳細（Problem 本文の detail/title）が
    あればそれを、無ければ従来の平易な文言へフォールバックし、いずれも最新を再読み込み。その他の
    エラーは `toMessages` で詳細優先表示。
  - テスト: 400 検証詳細の表示・409 詳細の表示を追加（既存 8 → 10）。
- 対象外:
  - SC-06（データソース登録の 400 等）への展開は本 PR では扱わない（issue 対応方針の「検討」。follow-up）。
  - BFF/バックエンドの変更（`ApiError.details` は既存基盤）。

## 受け入れ基準（Issue #177）との対応

- [x] 作成/編集フローで `ApiError.details` を用いて検証・競合の詳細を表示する（SC-09 と UX 統一）。
- [x] 版競合（details 空）は従来の平易な文言＋再読み込みを維持する（回帰なし）。
- [x] `npm run test` / `typecheck` / `lint` / `test:coverage`（ラチェット床）が通る。

## テスト観点

- 作成: 400（`details=["タイトルは必須です。"]`）で詳細が `role="alert"` に表示される。
- アーカイブ: 409（`details=["公開済みの文書はアーカイブできません。"]`）で詳細が表示される。
- 既存: 版競合（details 空）で「競合しました…」文言＋再読み込み（回帰維持）。
