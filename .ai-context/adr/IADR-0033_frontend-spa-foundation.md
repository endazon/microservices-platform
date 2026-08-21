---
title: IADR-0033 フロントエンド SPA 基盤 — React + TS + Vite・foundation/features 分離・BFF 境界・OIDC(PKCE)
type: impl-adr
status: Superseded
related_ids:
  - SC-01
  - SC-11
  - FR-15
  - ADR-0004
  - IADR-0009
  - ADR-0031
  - IADR-0121
author: claude
created: 2026-07-08
updated: 2026-08-04
plan_refs:
  - planning:projects/microservices-platform/05_screens
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0033: フロントエンド SPA 基盤

- 状態: **Superseded**（by [IADR-0121](./IADR-0121_spa-stack-migration-staging.md)・2026-08-04）
- 日付: 2026-07-08
- 決定者: claude（Issue #126。フレームワーク・認証・配置はユーザー判断）

## 起点・関連

- 画面: SC-01..SC-11（`05_screens`）。本基盤の上に各画面を feature として順次実装する（#127..#140）。
- 関連 ADR: ADR-0004（Keycloak OIDC）・[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）・
  FR-15/BFF（構成情報 API 等の後段集約）。

## 追補（2026-08-04）— 本 IADR は IADR-0121 により Superseded である

計画 ADR-0031（計画リポ）（Accepted）が
フロントエンドスタックを **React 19 + Vite + TanStack** に確定し、その追補（2026-07-30 の利用者裁定・planning#78）が
「実装リポジトリ側で `IADR-0033` の Superseded 化と後継 IADR の起票が必要である」と申し送った。これを受け、
後継は [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) である。以下の本文は**記録として残置**する。

| 本 IADR の決定 | 後継での扱い |
| --- | --- |
| 決定 1（React 18 + TS + Vite） | **置換**（React 19 + TanStack。IADR-0121 決定 1・移行第 1／第 2 段） |
| 決定 2（配置 `frontend/`） | **置換済み**（FR-14 / [IADR-0056](./IADR-0056_repo-unit-structure-platform-knowledge.md) が `src/<unit>/frontend` へ移動済み。さらに `src/packages/ui` を追加。IADR-0121 決定 4） |
| 決定 3（foundation / features 分離） | **継承**（Bulletproof React の Feature First と両立する。合成点の契約は第 2 段で見直す） |
| 決定 4（OIDC public client + PKCE・`oidc-client-ts`） | **置換予定**（ADR-0032（計画リポ） の BFF セッション方式へ。IADR-0121 決定 6・移行第 3 段／#439） |
| 決定 5（BFF を境界に疎結合・実行時 config） | **継承・強化**（orval 生成物の HTTP 出口を `foundation/api` の mutator へ集約。IADR-0121 決定 3） |
| 決定 6（存在秘匿・401 導線・ErrorBoundary） | **継承**（`ApiError` / 404 の扱いは新スタックでも変えない） |
| 決定 7（配信・CI） | **更新**（CI は pnpm 化。IADR-0121 決定 2） |

## コンテキストと課題

本リポジトリにフロントエンド基盤が存在せず、SC-11 未決事項 6 でも「他 SC 画面群の実装方針に合わせる」と
先送りされていた。全画面実装の前提として、フレームワーク・認証・BFF 接続・配置・共通方針を確定する。

## 決定

1. **技術スタック（ユーザー判断）**: **React 18 + TypeScript(strict) + Vite**。テストは Vitest + Testing Library
   （単体）と Playwright（スクリーンレベル e2e）。lint は ESLint(typescript-eslint)。
2. **配置（ユーザー判断）**: リポジトリ直下 **`frontend/`**。Node/Vite ツールチェーンを .NET の `src/` と分離する。
3. **基盤/可変の分離（バックエンドと同型）**: `src/foundation/`（安定・横断: 認証・ルーティング・API クライアント・
   UI 共通部品・実行時 config）と `src/features/<画面>/`（可変: 画面ごとの feature モジュール）。各 feature は
   `FeatureModule`（`routes`）を公開し、`src/features/index.ts` へ 1 行登録するだけで認証済みレイアウト配下に
   マウントされる。SC-01..11 はこの骨組みに 1 つずつ載る。
4. **認証（ユーザー判断）**: **Keycloak OIDC public client + Authorization Code + PKCE(S256)**（`oidc-client-ts`）。
   realm に public client `spa-web`（redirect `http://localhost:3100/*`）を追加。取得した JWT を BFF へ **Bearer**
   送信する（既存 BFF の JWT 検証にそのまま適合。バックエンド改修不要）。トークンは **localStorage へ永続化せず**
   メモリ保持（XSS 時の持ち出し面を狭める）。失効前の更新は **リフレッシュトークン**による silent renew で行う
   （Authorization Code フローは refresh_token を発行するため iframe を用いない）。更新に失敗した場合は、
   後述の 401 導線で再ログインへ誘導する。
5. **BFF を境界に疎結合**: features は `apiFetch`（`/bff/*`）経由でのみバックエンドへアクセスする。BFF ＋ OpenAPI が
   契約。接続先（BFF・Keycloak）は **実行時 config**（`window.__APP_CONFIG__`、`config.js`）で注入し、**同一ビルド
   成果物を任意環境へデプロイ**できる（コンテナ起動時に `config.js.template` を envsubst で生成）。dev は Vite proxy
   が `/bff` を BFF へ転送する。
6. **存在秘匿・エラー方針（IADR-0009 と整合）**: `ApiError` が HTTP ステータスを種別へ写像し、**404 は notFound**
   として扱い「不在」と「権限による秘匿」を画面で区別しない。**401 は `apiClient` の共通導線（`setUnauthorizedHandler`）
   で再ログインへ誘導**し（features 個別実装に依存しない骨組みレベルで担保）、共通 `ErrorBoundary` が想定外例外を握る。
7. **配信・CI**: 本番は multi-stage Docker（Vite build → nginx 静的配信＋`/bff` プロキシ）。CI（`frontend.yml`）は
   typecheck / lint / unit test / build ＋ Playwright スモーク（バックエンド不要のログイン画面到達）を実行する。

## 検討した代替

- **認証を BFF ブローカー（httpOnly cookie）にする**案: 最も安全だが BFF に新規のログイン/セッション/トークン更新
  エンドポイントが必要で、既存 BFF（JWT bearer 検証）への追加改修が大きい。public client + PKCE は既存 BFF に
  そのまま適合するため採用（トークンのブラウザ保持は非永続＋短命＋silent renew で緩和）。
- **配置を `src/Frontend/` や `apps/web` + `packages/`** にする案: 前者は .NET と Node のツールチェーン混在、後者は
  現時点で過剰。`frontend/` 単一ワークスペースを採用。

## 結果

- 良い影響: SC-01..11 が feature として 1 つずつ着手可能な骨組みが確定。バックエンドと BFF/OpenAPI で疎結合し、
  接続先は実行時 config で差し替え可能。認証・存在秘匿・CI が基盤として整う。
- トレードオフ: public client のためトークンがブラウザに存在する（非永続・短命・silent renew で緩和）。将来、
  機密度が上がれば BFF ブローカー方式へ移行可能（api クライアントの token provider 差し替えで局所化済み）。

## 関連

- Supersedes: なし
- Superseded by: [IADR-0121](./IADR-0121_spa-stack-migration-staging.md)（2026-08-04・#446）
