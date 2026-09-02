# フロントエンド（SPA）— platform / knowledge ユニット構成

マイクロサービスプラットフォーム基盤の SPA フロントエンド。pnpm workspace（ルート = `src/`）で
**platform/frontend（基盤: アプリホスト + foundation）** と
**knowledge/frontend（付随する可変機能: ナレッジ画面群）** を分離する
（FR-14 / [IADR-0033](../../../.ai-context/adr/IADR-0033_frontend-spa-foundation.md) /
[IADR-0056](../../../.ai-context/adr/IADR-0056_repo-unit-structure-platform-knowledge.md)）。

## スタック

React 19 + TypeScript(strict) + Vite / TanStack Router / BFF セッション認証（ADR-0032。OIDC は
BFF が実施し、SPA はトークンを扱わない）/ Vitest + Testing Library（単体）/ Playwright（e2e スモーク）/ ESLint。

## 構成（ユニット分離）

```
src/                             # pnpm workspace ルート（lock・eslint・vitest はここ）
  package.json                   # ルートスクリプト（dev/build/typecheck/lint/test/test:e2e）
  vitest.config.ts               # 単体テスト＋カバレッジ（全ユニット横断・しきい値ゲート）
  platform/frontend/             # 基盤ユニット（アプリホスト）
    src/                         # 計画 13_frontend-stack §ディレクトリ構成 のツリーに適合
      app/                       # アプリケーション層: providers / router / App.tsx / Layout.tsx（共通シェル）
      config/                    # 実行時 config（shared。原典が app の兄弟に置く区分）
      components/                # 共通コンポーネント（ui / notifications / ai-chat）
      lib/                       # api（orval 生成物と HTTP 出口）/ auth / i18n（設定済み Lingui）
      utils/                     # 自前の共有ユーティリティ関数（apiErrors / formatDateTime）
      testing/                   # 横断 setup と画面テスト用ハーネス（テスト専用の第 4 層）
      features/index.ts          # ユニット合成点（可変ユニットの features を束ねる。層としては app）
      locales/                   # ja / en の Lingui カタログ（実体あり。生成物はコミットする）
      assets/ hooks/ stores/ types/          # 🔴 中身が無い（.gitkeep のみ）。下の注記を読むこと
      main.tsx                   # エントリ
    index.html / vite.config.ts / e2e/ / public/
    nginx.default.conf.template / config.js.template   # 配信・実行時 config
  knowledge/frontend/            # 可変機能ユニット（ナレッジ画面群）
    src/features/<screen>/       # home, sc01..sc11。FeatureModule を公開し features/index.ts へ登録
  <unit>/frontend/               # 追加の可変機能ユニット（git submodule でリンク）
```

- 🔴 **空の区分（`.gitkeep` のみ）について** ——
  `platform` は `assets/ hooks/ stores/ types/`、`knowledge` は
  `app/ assets/ hooks/ locales/ stores/ testing/ types/ utils/` が空である。
  **「枠があること」を適合の証拠として読まないこと。**
  - **なぜ空か**（区分ごとに理由が違う）: `assets/` は外部 CDN と Web フォントを禁じた結果
    フォントがシステムフォント・アイコンがパッケージになり**置くものが無い**。`hooks/` の横断フックは
    **関心の隣に置いてある**（`lib/auth/useAuth.ts` 等）。`stores/` の Zustand は 1 本だけで、
    その 4 つの参照元がすべて `components/ai-chat/` 配下にある。`types/` は表示型が**生成 DTO**である。
    `knowledge` の `app/` `locales/` `testing/` は**アプリホスト（platform）が持つ**という意図的な不在で、
    同ユニットの `utils/` は**自前の純粋関数をまだ 1 つも持たない**（echarts の読み込み口は
    「設定済みライブラリを外へ渡す」形なので `lib/echarts/` にある。
    [IADR-0331](../../../.ai-context/adr/IADR-0331_non-rendering-module-placement.md) 決定 2）。
    - **［2026-09-02 追記 / #1131］`platform` の `utils/` はこの一覧から外れた。** 従前ここには
      「`utils/` の純粋関数は `components/ui/` に居る」と書いてあった —— **空だった理由は
      「置くものが無いから」ではなく、置くべきものが `components/` に居たからである。**
      実体（`apiErrors.ts` / `formatDateTime.ts`）を移したので、この区分はもう空ではない。
  - **消してよいかは未確定である。** planning#445 は**これらのディレクトリ名を名指しで**
    「ツリー全体への適合が必須」と裁定した一方、同じ裁定が「名前だけを揃える対応は採らない」とも言う。
    計画 `ADR-0065` 決定 4 は `.gitkeep` の枠置き規範を撤回したが、その明文はバックエンド標準の
    部分改定である。**裁定を planning#510 で求めている。答えが出るまで消さない。**
  - **従前ここには「枠のみ（`.gitkeep`。消さない）」とだけ書いてあった。**
    無条件の指示に読めるうえ、実体のある `locales/` まで枠の側に並べていた（誤り）。

- **エイリアス**: `@foundation/<区分>` は **platform 基盤の公開面の名前**であり、ディレクトリ名ではない。
  向き先は `config` → `src/config`、`routing` → `src/app/routing`、`api` / `auth` / `i18n` → `src/lib/*`、
  `utils` → `src/utils`、`ui` / `notifications` / `ai-chat` → `src/components/*`、`testing` → `src/testing`。
  ほかに `@knowledge` → `knowledge/frontend/src`、`@features` → `platform/frontend/src/features`（合成点）。
  **エイリアス名は変えない**（submodule の可変ユニットと `templates/unit-template` の契約が割れるため）。
  🔴 **「変えない」は改名の禁止であって、区分が増えたときに面を足すことは禁じていない**
  （[IADR-0331](../../../.ai-context/adr/IADR-0331_non-rendering-module-placement.md) 決定 4。
  `@foundation/utils` は #1131 で足した）。
  定義は `platform/frontend/tsconfig.app.json` / `knowledge/frontend/tsconfig.json` /
  `templates/unit-template/frontend/tsconfig.json` / `platform/frontend/vite.config.ts` /
  `src/vitest.config.ts` の **5 箇所**にあり、**5 つとも同じ向き先を持たせる**
  （**従前ここには「3 箇所」と書いてあったが誤りである** —— knowledge ユニットと unit-template も
  同じ `@foundation/*` の面を宣言している。片方だけ足すと
  「型検査は通るがビルド／テストだけ壊れる」形になる）。
  **ESLint の依存方向の規則もこのエイリアスを解決する** —— `src/eslint-import-resolver-unit-alias.cjs` が
  tsconfig の `paths` を読むので、向き先を足す・変えるときも ESLint 側に表を書き足す必要は無い。
- **層と依存の向き**: `shared`（`components` / `hooks` / `lib` / `stores` / `types` / `utils` / `config` /
  `assets` / `locales`）→ `features` → `app` の一方向。`testing/` はテスト専用の第 4 層で、
  `shared` と `app` を参照してよいが `features` は参照せず、**本番コードから参照されない**。
  **合成点（`features/index.ts`）は置き場所こそ `features/` 直下だが層としては `app`** である。
  すべて `eslint.config.js` の `import/no-restricted-paths` が両ユニットへ機械強制する。
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
