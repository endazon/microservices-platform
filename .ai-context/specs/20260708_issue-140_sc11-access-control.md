---
title: SC-11 アクセス制御（存在秘匿）の画面適用とテスト展開（Issue #140）
type: spec
status: draft
related_ids:
  - SC-11
  - FR-15
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: SC-11 アクセス制御・テスト展開（Issue #140）

> Wave A 3 件目（SC-11 UI 群）の 3/3。#137（グラフ）・#138（ドリフト）に続く仕上げ。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（管理者・運用者限定）
- 画面（SC）: SC-11 構成ビューア
- 関連 ADR: [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）／ [IADR-0030](../adr/IADR-0030_operator-role-and-config-viewer-policy.md)（ConfigViewer）／ [IADR-0035](../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md)（ロール別 nav・存在秘匿）
- Issue: #140（親 #122）

## 目的・背景

SC-11 のアクセス制御（管理者・運用者限定＋存在秘匿）を画面に適用し、テスト観点を `docs/tests/` へ
展開・実装する。ルート／ナビのロールゲートは #137 で `RequireRole[platform-admin, platform-operator]`
＋ `nav.requiresAnyRole` として導入済み。本 issue は**それを存在秘匿の観点で明示的にテストし、SC-11
テスト仕様書を作成**する（メニュー非表示・直接遷移時の NotFound・構成 API 未呼出）。

## 対象範囲

- 対象:
  - アクセス制御の画面テスト（`features/sc11-config/access.test.tsx`）: 管理者許可・運用者許可・権限外 NotFound（存在秘匿）・権限外は構成 API 未呼出。
  - ナビ存在秘匿テスト（`Layout.test.tsx` に構成ビューアのロール別表示を追加）。
  - SC-11 テスト仕様書（`docs/tests/SC-11_configuration-viewer.md`）の作成（#137/#138/#140 通貫）。
- 対象外:
  - 新規の画面実装（ゲート自体は #137 で導入済み）。BFF/バックエンド変更。
  - サーバ側 404 秘匿テスト（既存・#118 で実測済）。

## 設計

- ルート要素 `sc11ConfigFeature.routes[0].element` は `RequireRole` でラップ済み。権限外は `NotFound`（存在秘匿。/login へ誘導しない）。
- ナビ `nav.requiresAnyRole=[platform-admin, platform-operator]`。`Layout` が権限外の項目を描画しない。
- 実効境界はサーバ（`/bff/admin/config` は 404 秘匿）。UI はメニュー非表示・NotFound の二重で存在を示さない。

## 受け入れ基準（Issue #140）

- [x] 管理者・運用者以外は画面にアクセスできず、存在も示されない
- [x] テスト仕様書が作成され、アクセス制御を含む画面テストが存在する

## テスト方針

- 単体（Vitest）: ルート要素をロール別に描画し、許可（admin/operator）・存在秘匿（権限外→NotFound・API 未呼出）・ナビ出し分けを検証。SC-11 テスト仕様書へ写像。
- E2E（Playwright）: 未認証 `/config`→`/login`（#137 で追加済み）。

## 計画書との差異

- 差異: なし。

## 未決事項

- なし。
