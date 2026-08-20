---
title: 作業仕様書 — フロントエンド SPA 基盤（スケルトン＋CI）
type: spec
status: done
related_ids:
  - SC-01
  - SC-11
  - ADR-0004
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/05_screens
related_specs:
  - ../adr/IADR-0033_frontend-spa-foundation.md
---

# 作業仕様書: フロントエンド SPA 基盤

Issue: #126（親: #121。SC-01..11 実装の前提）。決定は [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)。

## 起点となる計画書（トレーサビリティ）

- 画面: SC-01..SC-11 / 関連 ADR: ADR-0004（Keycloak OIDC）・IADR-0009（存在秘匿）

## 目的・背景

全画面（SC-01..11）実装の前提となる SPA 基盤を確定し、**動くスケルトン＋CI（ビルド/テスト）**まで整える。
フレームワーク（React+TS+Vite）・認証（Keycloak public client + PKCE）・配置（`frontend/`）はユーザー判断。

## 対象範囲

- 対象:
  1. `frontend/`（React+TS+Vite）。`src/foundation/`（config/auth/api/routing/ui）＋ `src/features/`（feature 登録簿）。
  2. 認証: `oidc-client-ts`（Authorization Code + PKCE）。realm に public client `spa-web` を追加。
  3. BFF 境界: `apiFetch`（`/bff/*`・Bearer・ApiError で 404→notFound）。接続先は実行時 config（`config.js`）。
  4. スケルトン: ログイン画面 → 認証ガード → home feature（`/bff/dashboard/summary` を呼ぶ実例）。
  5. 配信: multi-stage Docker（Vite build → nginx 配信＋`/bff` プロキシ＋config.js を envsubst 生成）。compose に `frontend`（3100）。
  6. CI: `.github/workflows/frontend.yml`（typecheck/lint/unit/build＋Playwright スモーク）。
- 非対象: SC-01..11 各画面の実装（各 sub-issue）・BFF 側の新規エンドポイント。

## 受け入れ基準

- [x] SPA 基盤方針が IADR-0033 に記録されている（フレームワーク・認証・BFF 接続・配置）。
- [x] 最小スケルトンが動作する（ビルド・CI 組み込み・Keycloak ログイン導線）。
- [x] 後続 SC 画面 sub-issue が本基盤上で着手可能（`features/index.ts` へ登録するだけ）。

## テスト・検証（実行済み）

- `npm run typecheck` / `npm run lint`: エラー無し。
- `npm run test`（Vitest）: 11 件緑（runtimeConfig 3・apiClient 5・RequireAuth 3）。
- `npm run build`（Vite）: 成功（dist 生成）。
- `npm run test:e2e`（Playwright chromium）: スモーク 1 件緑（未認証で /login へ誘導・サインインボタン表示）。
- `docker compose config` VALID・realm JSON valid。
