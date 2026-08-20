# フロントエンド（SPA）— platform / knowledge ユニット構成

マイクロサービスプラットフォーム基盤の SPA フロントエンド。pnpm workspace（ルート = `src/`）で
**platform/frontend（基盤: アプリホスト + foundation）** と
**knowledge/frontend（付随する可変機能: ナレッジ画面群）** を分離する
（FR-14 / [IADR-0033](../../../.ai-context/adr/IADR-0033_frontend-spa-foundation.md) /
[IADR-0056](../../../.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## スタック

React 18 + TypeScript(strict) + Vite / React Router / oidc-client-ts（Keycloak OIDC public client + PKCE）/
Vitest + Testing Library（単体）/ Playwright（e2e スモーク）/ ESLint。

## 構成（ユニット分離）

```
src/                             # pnpm workspace ルート（lock・eslint・vitest はここ）
  package.json                   # ルートスクリプト（dev/build/typecheck/lint/test/test:e2e）
  vitest.config.ts               # 単体テスト＋カバレッジ（全ユニット横断・しきい値ゲート）
  platform/frontend/             # 基盤ユニット（アプリホスト）
    src/
      foundation/                # 安定・横断（config/auth/api/routing/ui。platform/backend に対応）
      features/index.ts          # ユニット合成点（可変ユニットの features を束ねる）
      main.tsx / App.tsx         # エントリ
    index.html / vite.config.ts / e2e/ / public/
    nginx.default.conf.template / config.js.template   # 配信・実行時 config
  knowledge/frontend/            # 可変機能ユニット（ナレッジ画面群）
    src/features/<screen>/       # home, sc01..sc11。FeatureModule を公開し features/index.ts へ登録
  <unit>/frontend/               # 追加の可変機能ユニット（git submodule でリンク）
```

- **エイリアス**: `@foundation` → `platform/frontend/src/foundation`、`@knowledge` → `knowledge/frontend/src`、
  `@features` → `platform/frontend/src/features`（合成点）。
- **BFF 境界**: バックエンドへは必ず `/bff/*` 経由（`foundation/api` の `apiFetch`）。
  接続先はビルドに焼き込まず実行時 config（`platform/frontend/public/config.js`）で注入する。

**新しい画面の追加（knowledge 内）**: `knowledge/frontend/src/features/<screen>/` に
`FeatureModule`（`routes`）を作り、`knowledge/frontend/src/features/index.ts` の `features` へ
1 行追加する。

**新しい可変機能ユニットの追加**: ユニットのリポジトリ（`frontend/package.json` + `frontend/src/features/`）
を `src/<unit>/` に submodule 配置し、`platform/frontend/src/features/index.ts`（合成点）へ
import を 1 行追加する（pnpm workspace の `'*/frontend'` により自動認識される。メンバの正本は
`src/pnpm-workspace.yaml` 自身で、IADR-0121 決定 2）。

## 開発（ワークスペースルート = `src/` で実行）

```bash
pnpm install
pnpm run dev        # http://localhost:3100 （/bff は BFF(5000) へプロキシ。VITE_BFF_TARGET で上書き可）
pnpm run typecheck  # 各ユニットの tsc
pnpm run lint
pnpm run test       # Vitest 単体（全ユニット横断）
pnpm run test:coverage  # カバレッジ（しきい値=回帰防止ラチェット）
pnpm run build      # 型チェック + 本番ビルド（platform/frontend/dist）
pnpm run test:e2e   # Playwright スモーク（ブラウザ未取得なら `pnpm exec playwright install chromium`）
```

Keycloak ログインには dev スタック（`docker compose -f deploy/docker-compose.yml up -d keycloak bff`）と、
realm の public client `platform-spa`（redirect `http://localhost:3100/*`。realm import 済み）が必要。
