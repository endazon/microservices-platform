---
title: SC-11 実効構成のグラフ表示（Issue #137）
type: spec
status: draft
related_ids:
  - SC-11
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
---

# 仕様書: SC-11 実効構成のグラフ表示（Issue #137）

> Wave A 3 件目（SC-11 UI 群）の 1/3。#138（ドリフト）・#140（アクセス制御・テスト展開）に先行する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-15（実効構成の可視化）
- 画面（SC）: SC-11 構成ビューア
- 関連 ADR: ADR-0018（Composable Architecture）／ [IADR-0029](../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md)（構成情報 API・ドリフト）／ [IADR-0030](../adr/IADR-0030_operator-role-and-config-viewer-policy.md)（ConfigViewer）／ [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)（SPA 基盤）／ [IADR-0035](../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md)（ロール別 nav・存在秘匿）／ **[IADR-0036](../adr/IADR-0036_sc11-config-viewer-visualization.md)（可視化方式・新規）**
- Issue: #137（親 #122）

## 目的・背景

構成ビューア（SC-11）の中核として、`/bff/admin/config`（`EffectiveConfigDto`、実装済）の実効構成
（構成バージョン・パイプライン段・イベント接続・ポート選択・コネクタ）を参照専用で可視化する。
SC-11 仕様書の**未決事項 4（グラフレイアウト）を解決**する（[IADR-0036](../adr/IADR-0036_sc11-config-viewer-visualization.md)）。

## 対象範囲

- 対象:
  - feature `features/sc11-config`（`/config` ルート）。`RequireRole anyOf=[platform-admin, platform-operator]`（ConfigViewer 相当）でゲート。ナビ「構成ビューア」を同ロールに限定表示。
  - 実効構成の描画: 構成バージョンヘッダ／パイプライン段（CSS チェーン・無効段グレーアウト）／イベント接続（表）／ポート選択（表）／コネクタ（一覧）。折りたたみセクション。
  - 404 秘匿・エラー・loading の状態表示。
  - 未決事項 4 の解決（[IADR-0036](../adr/IADR-0036_sc11-config-viewer-visualization.md)）と SC-11 仕様書の更新。
  - テスト: Vitest（取得・描画・404 秘匿・異常系）、Playwright スモーク（未認証 `/config`→`/login`）。
- 対象外:
  - ドリフト表示（#138）、履歴（#139）、アクセス制御のテスト観点展開（#140）。
  - BFF/バックエンド変更（`/bff/admin/config` は実装済）。

## 設計

- `apiFetch<EffectiveConfig>('/admin/config')`（→ `/bff/admin/config`）。404→notFound（秘匿）、他は error。
- 可視化: グラフ描画ライブラリ非導入。CSS チェーン＋表＋`<details>` 折りたたみ（[IADR-0036](../adr/IADR-0036_sc11-config-viewer-visualization.md)）。
- ゲート: ルート・ナビとも ConfigViewer 相当。権限外はルートで `NotFound`（存在秘匿）、ナビ非表示。サーバも 404 秘匿。

## 受け入れ基準（Issue #137）

- [ ] 実効構成（段・イベント接続・ポート選択・コネクタ・構成バージョン）がグラフ・一覧として閲覧できる
- [ ] レイアウト方針が決定され SC-11 仕様書に反映されている（未決事項 4 → [IADR-0036](../adr/IADR-0036_sc11-config-viewer-visualization.md)）

## テスト方針

- 単体（Vitest）: `apiFetch` をモックし、`/admin/config` 呼び出し・各領域の描画・無効段・404 秘匿・5xx alert。
- E2E（Playwright, バックエンド不要）: 未認証 `/config`→`/login`。

## 計画書との差異

- 差異: なし（ドリフト種別の固定表記は #138 で実データ `DriftFindingDto` の kind/severity に合わせて汎用描画にする方針を先出し）。

## 未決事項

- なし（#137 スコープ内）。ドリフト・履歴・アクセス制御テストは後続 issue。
