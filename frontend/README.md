# Knowledge Platform — Frontend (SPA)

SC-01..SC-11 の SPA 基盤（Issue #126）。方針は [IADR-0033](../docs/adr/IADR-0033_frontend-spa-foundation.md)。

## スタック

React 18 + TypeScript(strict) + Vite / React Router / oidc-client-ts（Keycloak OIDC public client + PKCE）/
Vitest + Testing Library（単体）/ Playwright（e2e スモーク）/ ESLint。

## 構成（基盤/可変の分離）

```
src/
  foundation/            # 安定・横断（バックエンドの Foundation に対応）
    config/              # 実行時 config（接続先の環境変数注入）
    auth/                # Keycloak OIDC（UserManager, AuthProvider, RequireAuth, /callback, /login）
    api/                 # BFF 境界（apiFetch: /bff・Bearer・ApiError で 404→notFound=IADR-0009）
    routing/             # features を束ねる router / FeatureModule 契約
    ui/                  # Layout, NotFound, ErrorBoundary
  features/<screen>/     # 可変（画面ごと）。FeatureModule を公開し features/index.ts へ登録
```

**新しい画面（SC-xx）の追加**: `src/features/<screen>/` に `FeatureModule`（`routes`）を作り、
`src/features/index.ts` の `features` へ 1 行追加する。認証済みレイアウト配下に自動でマウントされる。

## 開発

```bash
npm install
npm run dev        # http://localhost:3100 （/bff は BFF(5000) へプロキシ。VITE_BFF_TARGET で上書き可）
npm run typecheck
npm run lint
npm run test       # Vitest 単体
npm run build      # 型チェック + 本番ビルド
npm run test:e2e   # Playwright スモーク（要 `npx playwright install chromium`）
```

Keycloak ログインには dev スタック（`docker compose -f deploy/docker-compose.yml up -d keycloak bff`）と、
realm の public client `spa-web`（redirect `http://localhost:3100/*`。realm import 済み）が必要。

## 接続先（実行時 config・疎結合）

ビルド成果物は環境非依存。接続先は実行時に注入する:

- dev: `public/config.js`（`window.__APP_CONFIG__`）＋ Vite proxy（`/bff` → BFF）。
- 本番: コンテナ起動時に `config.js.template` を envsubst で `config.js` へ生成する
  （`BFF_BASE_URL` / `OIDC_AUTHORITY` / `OIDC_CLIENT_ID`）。nginx が `/bff` を `BFF_UPSTREAM` へプロキシする。
