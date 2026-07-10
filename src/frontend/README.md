# フロントエンド（SPA）— platform / knowledge ユニット構成

マイクロサービスプラットフォーム基盤の SPA フロントエンド。npm workspaces で
**platform（基盤: アプリホスト + foundation）** と **knowledge（付随する可変機能: ナレッジ画面群）**
を分離する（FR-14 / [IADR-0033](../../docs/adr/IADR-0033_frontend-spa-foundation.md) /
[IADR-0056](../../docs/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## スタック

React 18 + TypeScript(strict) + Vite / React Router / oidc-client-ts（Keycloak OIDC public client + PKCE）/
Vitest + Testing Library（単体）/ Playwright（e2e スモーク）/ ESLint。

## 構成（ユニット分離）

```
src/frontend/                # npm workspaces ルート（lock・eslint・vitest はここ）
  package.json               # ルートスクリプト（dev/build/typecheck/lint/test/test:e2e）
  vitest.config.ts           # 単体テスト＋カバレッジ（全ユニット横断・しきい値ゲート）
  platform/                  # 基盤ユニット（アプリホスト）
    src/
      foundation/            # 安定・横断（config/auth/api/routing/ui。バックエンドの platform に対応）
      features/index.ts      # ユニット合成点（可変ユニットの features を束ねる）
      main.tsx / App.tsx     # エントリ
    index.html / vite.config.ts / e2e/ / public/
    nginx.default.conf.template / config.js.template   # 配信・実行時 config
  knowledge/                 # 可変機能ユニット（ナレッジ画面群）
    src/features/<screen>/   # home, sc01..sc11。FeatureModule を公開し features/index.ts へ登録
  <unit>/                    # 追加の可変機能ユニット（git submodule でリンク）
```

- **エイリアス**: `@foundation` → `platform/src/foundation`、`@knowledge` → `knowledge/src`、
  `@features` → `platform/src/features`（合成点）。
- **BFF 境界**: バックエンドへは必ず `/bff/*` 経由（`foundation/api` の `apiFetch`）。
  接続先はビルドに焼き込まず実行時 config（`platform/public/config.js`）で注入する。

**新しい画面の追加（knowledge 内）**: `knowledge/src/features/<screen>/` に `FeatureModule`（`routes`）を
作り、`knowledge/src/features/index.ts` の `features` へ 1 行追加する。

**新しい可変機能ユニットの追加**: ユニットのリポジトリ（`package.json` + `src/features/`）を
`src/frontend/<unit>/` に submodule 配置し、`platform/src/features/index.ts`（合成点）へ
import を 1 行追加する（workspaces は `"*"` のため自動認識される）。

## 開発（ワークスペースルート = `src/frontend/` で実行）

```bash
npm install
npm run dev        # http://localhost:3100 （/bff は BFF(5000) へプロキシ。VITE_BFF_TARGET で上書き可）
npm run typecheck  # 各ユニットの tsc
npm run lint
npm run test       # Vitest 単体（全ユニット横断）
npm run test:coverage  # カバレッジ（しきい値=回帰防止ラチェット）
npm run build      # 型チェック + 本番ビルド（platform/dist）
npm run test:e2e   # Playwright スモーク（要 `npx playwright install chromium`）
```

Keycloak ログインには dev スタック（`docker compose -f deploy/docker-compose.yml up -d keycloak bff`）と、
realm の public client `spa-web`（redirect `http://localhost:3100/*`。realm import 済み）が必要。
