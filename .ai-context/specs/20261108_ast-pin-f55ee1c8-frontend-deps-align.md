---
title: 作業仕様書 — AST の submodule を f55ee1c8 へ進め、AST の新しい依存の宣言に合わせて MSP の @tanstack/react-query と @playwright/test を上げる
type: spec
status: done
related_ids: [NFR, IADR-0121, IADR-0232]
author: claude
created: 2026-11-08
updated: 2026-11-08
---

# 作業仕様書 — AST の submodule を f55ee1c8 へ進め、フロントの依存の割れを塞ぐ

> 本仕様書は実装着手前に作成した（着手 2026-11-08）。起点は MSP `origin/develop` `f29b40261`。AST の pin は `c38b3301` から `f55ee1c8`（AST の `origin/develop` の先頭）へ進める。

## 起点（トレーサビリティ）

- 非機能（`NFR`）。AST の develop に、MSP の lockfile より新しい依存の宣言が入った。
  - `@tanstack/react-query`: AST は `^5.105.3`（AST#1576）。MSP の lock は `5.101.4`。
    pin を進めると実体が 2 つに割れ、MSP の単体試験 11 件が `No QueryClient set` で落ちる
    （`QueryClientProvider` と `useQueryClient` が別の実体の React コンテキストを見るため）。
  - `@playwright/test`: AST は `^1.64.1`（AST#1602）。MSP は `1.62.1`。2 版になり、
    `platform/frontend/e2e` の `Page` 型が合わず typecheck と build が落ちる。
  - `@lingui/*` は AST 側が MSP の 6.6 を満たす範囲にしてあるので割れない。vite・vitest は MSP の
    `pnpm.overrides` が固定している。
- 利用者の裁定（2026-11-07）: **MSP の lockfile 側を上げて合わせる**。

## 方式

- MSP の importer（`platform/frontend`・`knowledge/frontend`）の範囲を AST の宣言の下限へ上げる。
  - `@tanstack/react-query`: `^5.101.0` → `^5.105.3`
  - `@playwright/test`（`platform/frontend` のみ）: `^1.47.2` → `^1.64.1`
  - 追加可変機能ユニットの雛形（`templates/unit-template/frontend`。workspace の importer）の
    `@tanstack/react-query` も `^5.105.3` へ上げる。`^5.101.0` のままだと lockfile の既存の解決（5.101.4）が
    雛形の importer に残り、実体が割れたままになるため。
- lockfile の解決は AST の lock と同じ版（`@tanstack/react-query` 5.105.3・`@playwright/test` 1.64.1）に揃える。
  AST の CI がその版で緑であり、出て数日の最新版を合成側だけで先に入れない。
- `pnpm dedupe` のような lockfile 全体への操作はしない。変える範囲は上の 2 系統と、pin の前進で AST の
  importer が新たに要する依存（AST の ESLint 10 系・vite 8 系の宣言など）に限る。
- `pnpm.overrides` へ足すことはしない（恒久の固定は方式の変更になる。裁定は「lockfile 側を上げる」）。
  よって新しい IADR は起こさない（pin の前進と依存の追随だけ）。
- 各パッケージの実体が 1 つであることを `pnpm ls -r --depth Infinity` で確かめる。

## 作業の範囲

1. submodule `src/ai-stock-trading` を `f55ee1c8` へ進める。
2. 上の範囲の変更と `src/pnpm-lock.yaml` の更新。
3. ［2026-11-08 追記］視覚回帰の入口（`platform/frontend` の `test:e2e:visual`）が起こす公式イメージを
   `mcr.microsoft.com/playwright:v1.62.1-noble` → `v1.64.1-noble` へ上げる。repo 試験（#2039）が
   「イメージの版 ＝ lockfile の `@playwright/test` の版」を固定しており、初回の
   `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` がこの食い違いで落ちた。視覚回帰を定めた
   実装 ADR の手順（版を上げる PR では `--update-snapshots` で撮り直して差分を見る）に従う。
4. 初期ロードの JS の上限（`check-chunk-budget --require`）を超えるなら、新旧の pin で同じ checkout を
   ビルドしてチャンクごとに突き合わせ、帰属を baseline の注記に残してから `--update` する。

## 受け入れ基準

- `pnpm install --frozen-lockfile` が通る。
- `@tanstack/react-query`・`@tanstack/query-core`・`@playwright/test`・`playwright`・`playwright-core` の実体が
  ワークスペースで各 1 つ。
- `typecheck`・`lint`・`format:check`・`test:coverage`（AST の試験ファイルを含む全件）・`build`・
  `check-chunk-budget --require`・`check-knip --require`・i18n の再生成で差分なし・e2e（流せる範囲）が緑。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が終了コード 0。文書の検査・`check-commit-messages` が緑。
- AST の `Directory.Build.props` に NuGet Audit（high / critical を復元の失敗にする）が入ったため、
  MSP の BFF（`Platform.Bff`。AST の `AiStockTrading.Bff.Endpoints` を参照する）の `dotnet build` / `test` が緑。

## 取り込む AST のコミット（`git -C src/ai-stock-trading log --oneline c38b3301..f55ee1c8`、13 件）

MSP が AST から取り込む口（SPA の合成・pnpm workspace の importer・BFF の参照・submodule のバックエンドのビルドと
Integration）に当たる範囲を数えた（`frontend/src`・`backend/Bff`+`backend/Shared`+`Directory.Build.props`・`frontend/package.json` の変更ファイル数）。

| コミット | 内容 | frontend/src | BFF・Shared・props | package.json | MSP への影響 |
| --- | --- | --- | --- | --- | --- |
| eb6ca12d（AST#1576） | `@tanstack/react-query` 5.105.3 | 0 | 0 | 1 | **要る**: 実体の割れ（本件） |
| b2a2e876（AST#1602） | `@playwright/test` 1.64.1 | 0 | 0 | 1 | **要る**: `Page` 型の割れ（本件） |
| e2c66f9c（AST#1595）・1e89cc10（AST#1572）・e9c36bfd（AST#1578） | ESLint 10 系・`eslint-plugin-import-x` | 1 | 0 | 1 | lockfile に AST の importer の依存が増える。MSP の lint はルートの ESLint 9 で走るので影響しない（下で確かめた） |
| baf41579（AST#1604） | vitest 5・plugin-react 6（vite 8）・`@lingui/*` 6.12 の開発ツール | 1 | 0 | 1 | vite・vitest は MSP の `pnpm.overrides` が 6.4 / 3.2 に固定する。実行時の `@lingui/core`・`@lingui/react` は全 importer で 6.6.0 のまま（6.12.0 の `@lingui/core` は AST の `@lingui/cli`・macro 6.12 の推移依存だけ） |
| 67e06c08（AST#1599）・b37b4849（AST#1574） | `@types/node` 26・user-event 14.6.8 | 0 | 0 | 1 | AST の importer だけが使う（typecheck で確かめた） |
| cb3bd489（AST#1597） | 実行環境とイメージの硬化。**AST の `Directory.Build.props` に NuGet Audit**（high / critical を復元の失敗にする）・Shared.Infrastructure の CA 信頼の撤去 | 0 | 5 | 0 | MSP の BFF が参照する `AiStockTrading.Bff.Endpoints` にも効く。restore・build・test で確かめた |
| 5c13606e（AST#1596） | 通知の受け口の硬化 | 0 | 0 | 0 | なし（AST のサービスの内部） |
| da194336（AST#1571）・a40319b5（AST#1593）・f55ee1c8（AST#1605） | dotnet/sdk のイメージ・CHANGELOG・AI レビュー | 0 | 0 | 0 | なし |

## 確かめたこと（手元・submodule を f55ee1c8 で取得した checkout）

- lockfile は、一時的に `pnpm.overrides` へ `@tanstack/react-query: 5.105.3`・`@playwright/test: 1.64.1` を入れて
  `pnpm install --lockfile-only` で解決し、overrides を戻して再度 `--lockfile-only` を掛けた（解決はそのまま残り、
  lockfile の `overrides:` にも残らない）。`pnpm dedupe` は掛けていない。
- 実体の数（`pnpm ls -r --depth Infinity --json` を集計）: `@tanstack/react-query` 5.105.3・`@tanstack/query-core` 5.105.3・
  `@playwright/test` 1.64.1・`playwright` 1.64.1・`playwright-core` 1.64.1・`@lingui/react` 6.6.0・`react` 19.2.8・
  `@tanstack/react-router` 1.170.18 がいずれも 1 つ。`@lingui/core` は 6.6.0 と 6.12.0 の 2 つだが、6.12.0 は
  AST の開発ツール（`@lingui/cli`・macro 6.12）の推移依存だけで、全 importer の直接の依存は 6.6.0。
- `pnpm install --frozen-lockfile`・`typecheck`・`lint`・`format:check` が緑。
- `test:coverage`: 170 ファイル・2172 件が緑（AST の試験ファイル 33 を含む。`No QueryClient set` は 0 件）。
- `build` が緑。`check-chunk-budget --require`: 初期ロード 660.51 kB（床 661.03 kB）・最大チャンク 586.04 kB（上限 600 kB）・
  必須チャンク 8 本が実在。床の更新は要らない。
- `check-knip --require`（床どおり 35 件）・`check-i18n-catalogs`・`pnpm run i18n` と `pnpm run codegen` の再生成で差分なし・
  `check-static-egress` が緑。
- e2e（`playwright test`。chromium 1248）: 224 件が緑。
- 視覚回帰（`pnpm run test:e2e:visual`。イメージ `v1.64.1-noble`・digest `sha256:2f05f8a7…8768f`）: 3 件が緑。
  `--update-snapshots=all` で 3 枚を撮り直しても基準画像のバイトは変わらなかった（`git status` に差分なし）。
  よって基準画像は更新しない。
- `experimental_streamedQuery` の再評価条件（実装 ADR の決定。`useAiChatStream.ts` の注記）: 5.105.3 の
  `query-core` の `index.d.ts` でも `streamedQuery as experimental_streamedQuery` のままで、条件 (1) は満たされない。
  決定は維持（本件で変えることは無い）。
- AST のバックエンド（MSP の CI の `submodule-backend-build` と同じ手順）: `dotnet restore --locked-mode`（NuGet Audit で
  止まらない）・`build -c Release`（警告 0・エラー 0）・`test --filter Category!=Integration` が緑。
- 文書・静的の検査（`check-trace-blocks`・`check-doc-links`・`gen-knowledge-graph --check`・`check-plan-id-qualification`・
  `check-reading-budget`・`check-realm-copy-drift`・`check-contract-schema`・`check-event-topology`・`check-unit-dependencies`・
  `check-unit-service-ownership`・`check-image-mapping --require-submodule` ほか）が緑。
  `check-deploy-manifests` は手元に helm / kubeconform が無いので流していない（CI が流す）。

## 母集合（規則 9）

- MSP 側で `@tanstack/react-query`・`@playwright/test` を宣言する package.json は
  `git grep -n '"@tanstack/react-query"\|"@playwright/test"' -- '*package.json'` で引いた
  （`src/platform/frontend`・`src/knowledge/frontend`・`templates/unit-template/frontend`。submodule の AST は除く）。
- 関連の `@tanstack/*`（`react-router`・`router-core`・`react-table`・`eslint-plugin-*`）は AST の宣言が変わっていない
  か MSP だけが持つので、上げない。`@tanstack/react-store` の 0.9.3 / 0.11.1 の 2 版は本件の前からあり
  （router と table の推移依存）、本件の範囲外。

## 残余

- 次に AST が依存を上げたときも同じ割れが起こり得る（AST の dependabot は MSP の lockfile を見ない）。
  恒久の固定（`pnpm.overrides`）は方式の変更になるので本件では入れない。
