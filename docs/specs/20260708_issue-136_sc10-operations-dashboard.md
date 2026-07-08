---
title: SC-10 運用ダッシュボード実装（Issue #136）
type: spec
status: draft
related_ids:
  - SC-10
  - UC-05
  - FR-10
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
---

# 仕様書: SC-10 運用ダッシュボード（Issue #136）

> 本仕様書は実装着手前に作成する。フロントエンド各画面フェーズの最初の 1 件。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（利用状況・検索傾向・回答品質のダッシュボード）
- ユースケース（UC）: UC-05（管理者・運用者による確認）
- 画面（SC）: SC-10 運用ダッシュボード
- 関連 ADR: [[IADR-0035]]（新規・ロールベース nav / 存在秘匿）、[[IADR-0033]]（SPA 基盤）、[[IADR-0011]]（ダッシュボード集約）、[[IADR-0009]]（存在秘匿）
- Issue: #136（親 #121）

## 目的・背景

SPA 基盤（#126）の上に SC-10 を最初の feature として実装する。BFF 集約 `/bff/dashboard/summary`（実装済・`AdminOnly`）を要約表示し、詳細分析ツール（Grafana/Jaeger/Kiali）と構成ビューア（SC-11）への導線を提供する。本画面の実装に伴い、後続画面（SC-09/SC-11）で再利用する**ロールベースのナビゲーション出し分けと存在秘匿**の共通部品を基盤へ導入する（[[IADR-0035]]）。

## 対象範囲

- 対象:
  - 基盤拡張: `foundation/auth/roles.ts`（realm ロール読み取り・`useRoles`/`useHasAnyRole`）、`foundation/auth/RequireRole.tsx`（存在秘匿ガード）、`FeatureModule.nav` によるナビ出し分け、`Layout` のロール別ナビ、`runtimeConfig` へ `opsLinks`（Grafana/Jaeger/Kiali URL）追加。
  - feature: `features/sc10-operations`（`/ops` ルート、サマリ表示、外部ツール導線、SC-11 導線）。
  - テスト: Vitest（roles / RequireRole / OperationsDashboardPage / runtimeConfig opsLinks / nav 出し分け）、Playwright スモーク（未認証 `/ops` → `/login`）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・[[IADR-0035]]。
- 対象外:
  - BFF/バックエンド変更（`/bff/dashboard/summary` は実装済のため不要）。
  - グラフ描画ライブラリ導入（数値・一覧・簡易バーで表現。高度可視化は Grafana に委譲）。
  - SC-11 本体（#137 以降）。本画面の SC-11 導線はリンクのみ。

## 設計

### API 境界
- `GET /bff/dashboard/summary?days&top` を `apiFetch<DashboardSummaryDto>` で取得。`ApiError` を `forbidden`(403)/`notFound`(404)/`error` に写像して中立表示。
- 外部ツール URL は `appConfig().opsLinks`（実行時 config）。未設定は非表示。

### 基盤拡張（[[IADR-0035]]）
- `roles.ts`: `access_token`(JWT) のペイロードを復号し `realm_access.roles` を返す。復号失敗は空配列（フェイルクローズ）。
- `RequireRole`: 権限外は `NotFound` を描画（リダイレクトしない＝存在秘匿）。
- `FeatureModule.nav?: { label, to, requiresAnyRole? }`。`foundation/routing/nav.ts` が集約、`Layout` がロールで絞って描画。
- `runtimeConfig`: `AppConfig.opsLinks?: { grafanaUrl?, jaegerUrl?, kialiUrl? }`。`config.js.template`・env fallback を追加。

### 権限
- `/ops` = `platform-admin` 限定（データソースが `AdminOnly` のため）。ナビも同様。SC-11 導線は `platform-admin`/`platform-operator`。

## 受け入れ基準

計画（Issue #136）より転記:

- [ ] 画面仕様書が作成され、計画の画面設計・対応 UC と整合している → `docs/screens/SC-10_operations-dashboard.md`
- [ ] ダッシュボードサマリが表示され、Grafana/Kiali/Jaeger への導線がある
- [ ] AdminOnly 制御が画面に適用されている（ナビ非表示＋直接遷移で存在秘匿＋サーバ 403 の中立表示）
- [ ] 権限外の情報が表示されない（ABAC・存在秘匿の画面適用）
- [ ] テスト観点が `docs/tests/` へ展開されている → `docs/tests/SC-10_operations-dashboard.md`

## テスト方針

- 単体（Vitest + Testing Library）: `AuthContext` を差し替えてロール別描画・存在秘匿・403/404/loading/ok を検証。`apiFetch` は `fetch` をスタブ、または当該モジュールを `vi.mock`。
- E2E（Playwright, バックエンド不要）: 未認証で `/ops` を開くと `/login` へ誘導される（ルート登録＋認証ガードのスモーク。#126 方針を踏襲）。
- `/verify` でビルド・typecheck・lint・単体・E2E を実行し合否を確認する。

## 計画書との差異

- 差異: あり（軽微）。計画では SC-10 を「管理者・運用者（UC-05）」とするが、要約データ `/bff/dashboard/summary` は `AdminOnly`（[[IADR-0011]]）である。本画面は **管理者限定**とし、運用者は SC-11 導線（ConfigViewer）で構成確認へ到達する形に整理した。運用者向けサマリの要否は計画側の判断事項として `/plan-feedback` 候補（本 PR ではフィードバックまで行わず、差異として記録）。

## 未決事項

- なし（Kiali 未配備は既定 URL 未設定＝非表示で吸収）。
