---
title: 作業仕様書 — SPA 移行第 5 段 運用系ツーリングの残り（Plop / ESLint プラグイン 3 種・#493）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0031
  - IADR-0121
  - IADR-0203
  - IADR-0211
  - IADR-0275
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs: []
---

# 仕様書: Plop と ESLint プラグイン 3 種の導入（#493）

## 起点となる計画書（トレーサビリティ）

- FR / UC / SC: なし（**NFR / 運用保守**。計画の非機能要件表に当たる番号が無く、
  `.claude/rules/traceability.repo.md`「無採番 `NFR`」の場合 2（メタ作業）に当たり**環流しない**）
- 関連 ADR: 計画 `ADR-0031`（SPA スタック。`06_technical/13_frontend-stack` §採用技術一覧が本作業の
  受け入れ基準）。実装 ADR は [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1（第 5 段 = 運用系）/
  [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md)（Renovate / Husky。決定 3 が lint-staged / Commitlint を「入れない」と定める）/
  [IADR-0211](../adr/IADR-0211_knip-scope-and-unused-ratchet.md)（Knip のスコープ）/ 本作業で新設する
  [IADR-0275](../adr/IADR-0275_plop-feature-generator-and-lint-plugin-scope.md)
- 関連 issue: #493（本件）/ #446（親）/ #768（第 5 段の切り出し 1・Renovate / Husky / Knip）/
  planning#468（Git Hooks 行の裁定依頼。**本 issue を塞いでいる**）
- 計画書リンク: 隣接クローン `../project-planning` の `origin/main` を `git fetch` して直読した
  （`projects/microservices-platform/06_technical/13_frontend-stack.md`。`updated: 2026-08-22`）

## 対象範囲

- 対象: `src/package.json`（devDependencies ＋ `gen` script）/ `src/pnpm-lock.yaml` /
  `src/plopfile.js`（新規）/ `src/plop-templates/feature/**`（新規）/ `src/eslint.config.js` /
  `src/.prettierignore`（`*.hbs` の除外）/ `.ai-context/adr/IADR-0275`（新規）＋ `.ai-context/adr/README.md` 索引 /
  本仕様書
- 対象外:
  - **`lint-staged` / `Commitlint`** —— [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3 が `Accepted` で「入れない」と定め、
    planning#468 で裁定依頼中。**本 issue は close しない。**
  - **`src/knowledge/**` と `src/platform/frontend/src/**`** —— 別エージェントが #917（SC-18）で作業中。
    ロックファイル（`src/pnpm-lock.yaml`）だけが交差点である。
  - **`src/ai-stock-trading`**（別プロジェクトの submodule。[IADR-0120]。本リポの規約を及ぼさない）
  - **`.github/workflows/`** —— 起動条件・必須チェックを変えない。新しい devDependency と ESLint 規則は
    既存の `frontend.yml` / `frontend-tests.yml` の `lint` / `typecheck` ジョブがそのまま拾う。
  - **Knip / Renovate / Husky** —— #768 で完了済み（実測で確認。下記 §母集合）。

## 母集合の引き直し（着手時に自分で引いた。issue 本文の数えは転記していない）

**除外パス**: `node_modules`（全 workspace）・`.git`・`src/coverage`。
**`src/ai-stock-trading` は除外していない**（走査には含め、結果の解釈で「submodule なので是正しない」と
判断した）。**拡張子で絞っていない。行フィルタで絞っていない。**

### 軸 1 — 計画 §採用技術一覧 の全行を宣言側（`package.json`）から突き合わせる

母集合は表の**データ行 31 行**（`| 分野 | 採用技術 |` の見出し行を除く）。突合先は pnpm workspace の
**メンバ全 6 本の `package.json`**（`src/package.json` / `platform/frontend` / `knowledge/frontend` /
`packages/ui` / `ai-stock-trading/frontend` / `../templates/unit-template/frontend`。
`src/pnpm-workspace.yaml` の 3 グロブから列挙した）。

| 表の行 | 実測 |
| --- | --- |
| Framework / CSS / UI / アイコン / Variant / Toast / Error Boundary | 宣言あり（React 19・Tailwind v4・shadcn/ui 派生 `@platform/ui`・lucide-react・cva/clsx/tailwind-merge・sonner・react-error-boundary） |
| Head 管理（★不採用 react-helmet-async） | **0 件**（不採用の完了条件を満たす） |
| サーバー状態 TanStack Query / ルーティング TanStack Router / クライアント状態 Zustand | 宣言あり |
| フォーム RHF / Zod / resolvers / DevTools | 宣言あり（`knowledge/frontend`） |
| i18n Lingui / API 型生成 orval / テーブル TanStack Table / チャート ECharts / 日付 dayjs | 宣言あり |
| 認証（★不採用 oidc-client-ts） | **`ai-stock-trading/frontend` に 1 件残**（計画 2026-08-22 裁定どおり **AST 側リポジトリの作業**。本リポからは是正しない） |
| Vitest / RTL / Playwright / MSW / Storybook | 宣言あり |
| Linter / Formatter（ESLint flat ＋ Prettier、**TanStack / Testing Library / Storybook / Lingui のプラグインを併用**） | ESLint / Prettier / `eslint-plugin-storybook` / `eslint-plugin-lingui` はあり。**`@tanstack/eslint-plugin-query`・`@tanstack/eslint-plugin-router`・`eslint-plugin-testing-library` は 0 件** ← **本作業の対象** |
| Git Hooks Husky | あり（`src/package.json` の `husky` ＋ `.husky/`） |
| Git Hooks（★不採用 lint-staged / Commitlint） | **0 件**（実装は「入れない」で一致するが、計画表は 2026-08-22 に「★不採用」へ改訂済み。§計画書との差異 参照） |
| 依存更新 Renovate | ルート `renovate.json`（devDependency ではなく GitHub App 設定。宣言 0 件は正常） |
| CI/CD GitHub Actions / カバレッジ Codecov（★任意） | Actions あり。Codecov は任意のため未導入で可 |
| Dead Code 検出 Knip | あり（`src/knip.jsonc` ＋ `scripts/check-knip.js`） |
| **Generator Plop.js** | **0 件** ← **本作業の対象** |
| パッケージ管理 pnpm | あり（`packageManager: pnpm@10.33.0`） |

### 軸 2 — パスから引く（拡張子で絞らない）

`find` をリポジトリ全体（`node_modules` / `.git` を prune）に掛けた:
`plopfile*` **0** / `plop-templates` **0** / `generators` **0** / `commitlint.config.*` **0** /
`.lintstagedrc*` **0**。`renovate.json` **1**（ルート）/ `.husky` **1**。

### 軸 3 — 誤りの側の語で全文走査（`grep -rIl`、パス除外のみ）

- `plop`（大小無視）→ **12 ファイル**。内訳は `.ai-context/specs/` 9 ＋ `.ai-context/adr/` 3 で
  **すべて「未導入である」旨の言及**であり、設定・実装は 1 件も無い。
- `eslint-plugin-testing-library` → **0** / `eslint-plugin-query` → **0** / `eslint-plugin-router` → **0**。
- `testing-library/`（利用側）→ 多数。うち **`src/{platform,knowledge,packages}` で 37 ファイル**、
  **`src/ai-stock-trading/frontend/src` で 15 ファイル**。

### 軸 4 — 規則の宣言側（ESLint の設定）から引く

`src/eslint.config.js` が `plugins` に登録しているのは `react-hooks` / `react-refresh` / `lingui` と
`storybook.configs['flat/recommended']` の 4 系統のみ。`src/eslint.templates.config.js` は
`eslint.config.js` から禁止リストを import する薄い入口で、プラグイン登録を持たない。
**「4 プラグイン系のうち 2 つ（TanStack・Testing Library）が落ちている」という issue 本文の記述と
独立に一致した。**

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `src/ai-stock-trading/**`（AST の 15 テストファイルを含む） | 別プロジェクトの submodule。本リポジトリからは変更できない（[IADR-0120]）。**走査には含めた**うえで、新規 ESLint 規則の適用範囲から外す（§設計 参照）。既存の `NO_LEGACY_ROUTER_PATHS` が同じ理由で AST を外しているのと同じ判断である |
| `node_modules` / `src/coverage` | 生成物・依存 |
| `src/platform/frontend/src/foundation/api/generated/` | `eslint.config.js` が既に `ignores` に持つ生成物（IADR-0121 決定 3） |
| `templates/unit-template/frontend` | `src/` の外にあり `eslint .` の射程外（`lint:templates` が別入口で見る）。**Plop の生成先候補としては含める** |

### 規則 10（この変更で新たに誤りになる自分の記述）の引き直し

`plop` / `Plop` を含む 12 ファイルのうち、**live な権威文書は 0 件**である（`.ai-context/specs/` と
`.ai-context/adr/` はいずれも凍結記録であり、本文へ後付け注記をしない）。`CLAUDE.md` / `AGENTS.md` /
`docs/**` / `scripts/README.md` を `plop` / `Plop` / `Generator` / `雛形.*生成` で走査 → **0 件**。
したがって追随して直すべき記述は無い。

## 設計

### 1. Plop（`src/plopfile.js` ＋ `src/plop-templates/feature/`）

**置き場所**は pnpm workspace ルート（`src/`）である。`plop` は devDependency としてルートに入り
（`src/package.json`）、`pnpm run gen` で起動する。knip は `plop` 依存があると `plopfile.{cjs,mjs,js,ts}`
を config として認識するため（`node_modules/knip/dist/plugins/plop/index.js` の `config` を実読して確認）、
**`knip.jsonc` へ追加の `entry` を書く必要は無い**。

**生成する形は、この repo に実在する feature の形に合わせる。** 参照した実物:

- `src/knowledge/frontend/src/features/sc04-wiki/`（最小の feature。`api/` `hooks/` `stores/` `types/` が
  `.gitkeep` のみ、`components/` と `routes/` に実体、`index.ts` が公開面）
- `src/knowledge/frontend/src/features/sc08-analysis/`・`sc09-admin-abac/`（同じ 6 区分）
- `templates/unit-template/frontend/src/features/sample/`（**雛形の正解形**。計画
  §ディレクトリ構成 の Feature 単位 6 区分をすべて備え、Lingui マクロ・`@platform/ui`・
  `renderUnitRoute` を使うテストまで揃っている）

生成物（`<unit>/frontend/src/features/<name>/`）:

| ファイル | 内容 |
| --- | --- |
| `index.ts` | 公開面。`routes/` の factory と nav を再輸出する |
| `routes/<camelName>Route.ts` | `createRoute` ＋ `lazyRouteComponent` の型付き factory ＋ `PlanNavItem`（`msg` マクロ） |
| `components/<PascalName>Page.tsx` | `@platform/ui` と Lingui `<Trans>` を使う最小画面 |
| `components/<PascalName>Page.test.tsx` | `renderUnitRoute` で**自 feature の factory だけ**を載せて描画・ナビ解決を検査 |
| `api/.gitkeep` `hooks/.gitkeep` `stores/.gitkeep` `types/.gitkeep` | 6 区分の枠（`sc04-wiki` と同形） |

**テストは自 feature の factory を直接載せる**（`renderUnitRoute((shell) => [createXRoute(shell)], …)`。
`sc11-config` のテストと同じ形）。`features/index.ts` へ**自動で追記しない** —— あちらはタプルと
ナビ配列の 2 経路で、`as const` を壊すと型安全が丸ごと失われる（IADR-0124 決定 1）。
代わりに `onComplete` 相当のメッセージで**配線の 3 行**を表示する。

**プロンプト**: `unit`（`src/*/frontend/src/features` と `templates/*/frontend/src/features` を実走査した
選択肢）/ `name`（feature ディレクトリ名）/ `title`（表示名・日本語）/ `routePath` / `navGroup`
（`user` / `personal` / `admin` / `ops`）/ `withNav`。

**`*.hbs` は `src/.prettierignore` へ足す。** Prettier は `.hbs` を glimmer パーサで整形するため、
Handlebars 式を含む TS/TSX の雛形を壊す（`format:check` も落ちる）。

### 2. ESLint プラグイン 3 種

| プラグイン | 適用する設定 | 適用範囲 |
| --- | --- | --- |
| `@tanstack/eslint-plugin-query` | `flat/recommended` | `platform` / `knowledge` / `packages`（**AST を除く**） |
| `@tanstack/eslint-plugin-router` | `flat/recommended` | 同上 |
| `eslint-plugin-testing-library` | `flat/react` | 上記のうち `**/*.{test,spec}.{ts,tsx}` のみ |

**AST（`ai-stock-trading/frontend/**`）を適用範囲から外す。** 本リポジトリからは是正できない別プロジェクトの
submodule であり（[IADR-0120]）、既存の `NO_LEGACY_ROUTER_PATHS` が同じ理由で AST を外している。
**これは「ルールを off にして黙らせる」ことではない** —— 直せない他リポジトリのコードへ本リポの規約を
及ぼさない、という既存の線引きに合わせるものである。

**既存コードに出た error は原則として直す。** ルールを緩めるのは、直せない構造的な理由があるものだけとし、
理由を `eslint.config.js` のコメントと [IADR-0275] に残す。件数と処理は §実測 に記録する。

## 受け入れ基準

1. `pnpm run lint` / `typecheck` / `format:check` / `test` / `build` が通る。
2. `node scripts/check-knip.js --require` が通る（**床を `--update` で上げない**）。
3. `pnpm run gen` で feature を実生成し、その生成物が `lint` / `typecheck` を通る（実測する）。
4. 計画 §採用技術一覧 の Linter 行「TanStack / Testing Library / Storybook / Lingui のプラグインを併用」と
   Generator 行「Plop.js」を満たす。
5. **完全一致には達しない** —— Git Hooks 行の lint-staged / Commitlint が planning#468 の裁定待ちで
   塞いでいる（§計画書との差異）。**#493 は close しない。**

## 計画書との差異

- 計画 §採用技術一覧 は 2026-08-22 の裁定で **lint-staged / Commitlint を「★不採用」へ改訂済み**であり、
  実装（IADR-0203 決定 3）と**一致している**。issue #493 本文（同日更新）は「表が採用と定めている・
  planning#468 で裁定依頼中」と書いており、**計画本文の現況とずれている**。
  **本作業では計画本文の現況を優先して読むが、issue の close 判断は行わない**（統括・利用者の判断領域）。
- `ai-stock-trading/frontend` の `oidc-client-ts` は計画が「AST 側リポジトリの作業」と明記しており、
  本作業の射程外である。

## 実測

### 1. ESLint プラグイン 3 種を素で入れたときの違反（2026-08-23）

`flat/recommended`（TanStack Query / Router）と `flat/react`（Testing Library）を**範囲を絞らずに**
入れて `eslint . -f json` を集計した。**新規プラグイン由来は 213 件**（ほかに既存の
`react-refresh/only-export-components` の warn 9 件）。

| 規則 | 自リポ | `ai-stock-trading`（submodule） |
| --- | ---: | ---: |
| `testing-library/prefer-screen-queries` | 15 | 144 |
| `testing-library/no-node-access` | 18 | 18 |
| `testing-library/no-container` | 6 | 0 |
| `testing-library/render-result-naming-convention` | 8 | 0 |
| `testing-library/no-manual-cleanup` | 2 | 0 |
| `@tanstack/query/no-unstable-deps` | 2 | 0 |
| **計** | **51** | **162** |

**TanStack Router の 2 規則は 0 件**、TanStack Query の残り 6 規則も 0 件だった。

### 2. 処理の内訳

| 処理 | 件数 | 中身 |
| --- | ---: | --- |
| **射程の確定（AST）** | 162 | `ai-stock-trading/**` を適用範囲から外した。別プロジェクトの submodule で本リポジトリからは是正できない（[IADR-0120]）。既存の `NO_LEGACY_ROUTER_PATHS` と同じ線引きである |
| **射程の確定（Playwright）** | 15 | `**/e2e/**` を Testing Library の適用範囲から外した。`prefer-screen-queries` が Playwright の `page.getByRole(...)` を誤認していたもので、**該当 13 ファイルは Testing Library を一切 import していない**（残る 2 件は AST の e2e） |
| **規則を off（理由つき・3 規則）** | 32 | `no-container` 6 / `no-node-access` 18 / `render-result-naming-convention` 8。理由は `src/eslint.config.js` の `TESTING_LIBRARY_RULE_OVERRIDES` と [IADR-0275] 決定 4 |
| **抑制ファイルで grandfather** | 4 | `@tanstack/query/no-unstable-deps` 2 ＋ `testing-library/no-manual-cleanup` 2。**すべて #917 が同時に触っている領域**（`src/knowledge/**` / `src/platform/frontend/src/**`）にあり本 PR では書き換えない。`src/eslint-suppressions.json`（file × rule の件数）で抑え、**同じファイルの新しい違反は error のまま**である |
| **直した** | 0 | **自分の領域（`packages/ui` 11 ＋ 6 件）に残った違反は、上の 2 規則を off にした結果すべて消えた。** ソースの書き換えは行っていない |

**残り 19 ＋ 9 = 28 規則は `error` のままで、違反 0 件である。**

### 3. Plop の実走（2026-08-23）

`plop-templates/` の作成後、**実際に生成して検証した**（検証後に生成物は削除済み・コミット対象外）。

```
$ cd src && node_modules/.bin/plop feature "../templates/unit-template/frontend" \
    "sc99-plop-smoke" "動作確認" "/plop-smoke" user
√  ++ .../sc99-plop-smoke/index.ts
√  ++ .../sc99-plop-smoke/routes/sc99PlopSmokeRoute.ts
√  ++ .../sc99-plop-smoke/components/Sc99PlopSmokePage.tsx
√  ++ .../sc99-plop-smoke/components/Sc99PlopSmokePage.test.tsx
√  ++ .../sc99-plop-smoke/{api,hooks,stores,types}/.gitkeep
```

| 検証 | 結果 |
| --- | --- |
| `pnpm -r run typecheck`（雛形ユニットを含む 5 プロジェクト） | **Done**（生成物込みで通る） |
| `pnpm run lint:templates` | **0 件** |
| `pnpm run format:templates` | **All matched files use Prettier code style** |
| `node_modules/.bin/vitest run sc99-plop-smoke` | **2 tests passed**（生成されたテストがそのまま緑になる） |

**変異試験**（規則が本当に効いていることの確認）: 生成物の `findByRole` を
`await waitFor(() => screen.getByRole(...))` へ書き換えると
`testing-library/prefer-find-by` が `lint:templates` で error になった。**規則は宣言だけでなく実際に効いている。**

### 4. Knip（床を上げていない）

`node scripts/check-knip.js --require` は落ちるが、**内訳は本作業と無関係である。**

```
Unused files (2)
knowledge/frontend/src/components/echartsGraphBundle.ts
knowledge/frontend/src/components/echartsGraphLoader.ts
```

この 2 件は**別作業（#917）の未追跡ファイル**である（`git status` で `??`）。
本作業が足した `plop` / ESLint プラグイン 3 種は**いずれも未使用として湧いていない**
（knip の plop プラグインが `plopfile.js` を config として解決し、ESLint プラグインは
`eslint.config.js` の import として使用済みになる）。区分ごとの件数は
`devDependencies 4 / exports 16 / types 17 / unlisted 1` で **`scripts/knip-baseline.json` の床と完全一致**しており、
**`--update` は実行していない。**

### 5. 併走作業（#917）の影響で落ちている検査

本作業とは無関係に、作業ツリーに同居している #917（SC-18）の作業中ファイルが原因で落ちるもの:

| 検査 | 落ちる理由 |
| --- | --- |
| `pnpm run typecheck` / `pnpm run build` | `knowledge/frontend/src/features/sc18-graph/api/useGraphView.ts`（`queryKey` 欠落）と `routes/sc18GraphRoute.ts`（`../components/GraphViewPage` 未作成） |
| `pnpm run format:check` | `sc18-graph/components/GraphLegend.tsx` ほか 3 ファイル |
| `node scripts/check-knip.js --require` | 上記 §4 |
| `node scripts/check-contract-schema.js` | `Knowledge.Contracts.Dtos.GraphNodeItemDto.IsPrivateNote` の追加 |
| `node scripts/check-test-traceability.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | `SC-18` のテスト仕様書（`docs/tests/SC-18_*.md`）が未作成 |
| `node scripts/check-adr-numbering.js` | **IADR-0274 が欠番。** 本作業は統括の割り当てにより `IADR-0275` を使った（`IADR-0274` は #917 に予約されている）。**#917 が着地すれば埋まる** |
| `check-deploy-manifests` / `check-stack-ready` | helm / kubectl が本環境に無い（既知の環境要因） |

`pnpm run lint`（**0 errors** / 既存 warn 9）・`pnpm run test`（**86 files / 1019 tests passed**）・
`pnpm install --frozen-lockfile`（**成功**）は通っている。

### 6. ロックファイル（importer 消失の確認）

`git submodule status` で `src/ai-stock-trading` が populate 済み（`9b9c676`）であることを確認してから
`pnpm add -D` を実行した。差分は **714 行の追加のみ・削除 0 行**で、`importers:` の 6 本
（`.` / `ai-stock-trading/frontend` / `knowledge/frontend` / `packages/ui` / `platform/frontend` /
`../templates/unit-template/frontend`）はすべて残っている。`pnpm install --frozen-lockfile` も成功した。

## 計画書との差異（追記）

- 受け入れ基準「採用技術一覧との**完全一致**」には**達していない**。Git Hooks 行の
  `lint-staged` / `Commitlint` を [IADR-0203](../adr/IADR-0203_renovate-husky-hook-scope.md) 決定 3 が `Accepted` で「入れない」と定めており、
  planning#468 の裁定待ちである。**#493 は close しない。**
