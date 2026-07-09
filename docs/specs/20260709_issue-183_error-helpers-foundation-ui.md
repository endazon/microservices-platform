---
title: 検証/競合エラー表示ヘルパ（toMessages/ErrorList）を foundation/ui へ集約（Issue #183）
type: spec
status: completed
related_ids:
  - SC-05
  - SC-06
  - SC-09
  - FR-09
  - IADR-0040
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md (SC-05/SC-06/SC-09)"
---

# 仕様書: 検証/競合エラー表示ヘルパを foundation/ui へ集約（Issue #183）

## 起点となる計画書（トレーサビリティ）

- 画面(SC): SC-05（文書管理）／SC-09（管理者設定 ABAC）／SC-06（データソース管理・今後）
- 機能要求(FR): FR-09（認可・検証エラーの提示）
- 関連 ADR: [[IADR-0040]]（`ApiError.details` 統一）
- Issue: #183（PR #182 AI レビュー指摘）

## 目的・背景

PR #182 の AI レビューで、`toMessages(err, fallback)` ヘルパと `ApiError.details` を `<ul role="alert">` で
一覧表示する `Errors` コンポーネントが `sc05-documents` と `sc09-admin-abac` に重複していると指摘された。
SC-06 展開で 3 画面目の複製が見込まれるため、`foundation/ui` へ集約し単一情報源にする。

CLAUDE.md「過剰な抽象化を避ける」方針と両立させ、**既に 2 画面で重複している実体のある共通部品のみ**を
最小抽出する（新たな汎用化・設定項目の追加はしない）。

## 集約時の仕様確定（差分のすり合わせ）

2 実装は完全同一でないため、集約版の仕様を以下に確定する。

### `toMessages(err, fallback)` — SC-09 の superset を採用

```ts
// details があれば details（最も具体的）→ ApiError の message → fallback の優先順。
if (err instanceof ApiError && err.details.length > 0) return err.details;
if (err instanceof ApiError && err.message) return [err.message];
return [fallback];
```

- SC-09 版（details → message → fallback）を採用。SC-05 版（details → fallback）に対し中間段（`ApiError.message`）を
  加えるが、これは「詳細が無い ApiError でも汎用文言よりサーバ由来メッセージを優先する」より情報量の多い挙動で、
  既存テストは全て details 付き ApiError もしくは非 ApiError（fallback）を検証しており回帰しない。

### `ErrorList`（現 `Errors`）— 赤色スタイルに統一

- `<ul role="alert" style={{ color: '#b00' }}>` に統一（SC-09 の表現を採用）。SC-05 は従来スタイル無しだったが、
  エラー一覧の視認性向上として赤色へ寄せる。`role="alert"` と `<li>` テキストは不変のため既存テストは回帰しない。

## 対象範囲

- 新設: `frontend/src/foundation/ui/ErrorList.tsx`（`toMessages` と `ErrorList` を export）。
- 単体テスト: `frontend/src/foundation/ui/ErrorList.test.tsx`（優先順位・空配列 null・role）。
- 移行: `sc05-documents/DocumentManagementPage.tsx` / `sc09-admin-abac/AdminAbacSettingsPage.tsx` の
  ローカル `toMessages`/`Errors` を削除し foundation を import。
- SC-06（`sc06-datasource`）は現状エラー詳細表示を持たないため本 issue では新規導入しない（将来使用時に共通部品を使う）。

## 受け入れ基準

- [ ] `foundation/ui/ErrorList.tsx` に `toMessages` / `ErrorList` を集約。
- [ ] SC-05 / SC-09 のローカル実装を削除し foundation import へ移行。
- [ ] `foundation/ui/ErrorList.test.tsx` 追加（優先順位・null・role の検証）。
- [ ] SC-05 / SC-09 の既存テストが回帰しない。
- [ ] `npm run typecheck` / `npm run lint` / 単体テストが通る。
- [ ] カバレッジ床（`vite.config.ts` thresholds）を割らない。

## 影響・リスク

純粋なリファクタで機能・IF 変更なし。挙動差の確定（上記）以外は等価。
