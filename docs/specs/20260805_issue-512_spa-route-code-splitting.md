---
title: SPA のバンドルをルート単位で分割する（初期チャンクの削減と 500 kB 警告の解消）
type: spec
status: done
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, IADR-0009, IADR-0056, IADR-0121, IADR-0124, IADR-0125, IADR-0134]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - ../adr/IADR-0134_spa-route-code-splitting-boundaries.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
  - ../tech/tech-requirements.md
  - ./20260804_issue-490_spa-router-shell.md
  - ./20260804_issue-502_sc01-03-search-flow.md
  - ./20260805_issue-503_sc05-08-admin-screens.md
  - ./20260805_issue-504_sc09-11-admin-ops-screens.md
---

# 仕様書: SPA のバンドルをルート単位で分割する

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。
> **内部設計の判断（分割境界・棄却した案・計測の解釈）は [[IADR-0134]] を正とする。**

## 起点となる計画書（トレーサビリティ）

- **NFR（非機能要件）**: 計画
  [02_requirements §非機能要件](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) の**性能**。
  本作業が動かすのは「初回表示までに読む JavaScript の量」であり、計画が数値目標を与えているのは
  検索・RAG・取り込みの応答時間である。**初期バンドルの上限値を計画は定めていない**——
  よって本作業の合否は計画の数値ではなく、**ビルドツール（Vite）の既定予算 500 kB/チャンク**と
  **実測の前後比較**で判定する（issue #512 §受け入れ基準 も同じ立て方である）。
- **ADR（計画）**: [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 / Vite / **TanStack Router** / TanStack Query / Tailwind v4 ＋ shadcn/ui / Lingui）／
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed）
- **画面（SC）**: 分割の単位が画面（ルート）であるため **SC-01〜SC-11** が対象になる
  （SC-04 は SPA 側の遷移導線のみ）。画面の内容は変えない。
- 関連 IADR: **[[IADR-0134]]（本作業の内部設計判断。本書と対で読む）**・
  [[IADR-0121]]（移行の 5 段）・[[IADR-0124]]（型付きルート木・合成点）・
  [[IADR-0125]]（UI プリミティブ・i18n）・[[IADR-0009]]（存在秘匿）・[[IADR-0056]]（ユニット分離）
- 本リポジトリの起点: **#512**（親 #446 / #454。出所は #490 / #496 / #502 / #503 / #504 の申し送り）

## 目的・背景

全画面の再実装（#502 / #503 / #504）が完了し、バンドルは 572 → 586 → **632.98 kB（gzip 190.04 kB）** まで
伸びて Vite の 500 kB 警告に毎ビルド触れている。ルート単位の遅延分割は「**どの画面がどれだけ重いかが
確定してから**」行うべきであるとして、上記 5 本の申し送りが一貫して先送りしてきた。全画面が揃った
いまが実施時期である。

**本作業の要点は「推測で分割しない」ことである。** 実測の前に分割方針を決めると、
「画面が重いはずだ」という思い込みで境界を引くことになる。実際には（§計測）**初期チャンクの
81.7% は依存**であり、ルート境界だけでは警告は消えなかった。

## 対象範囲

### 対象

1. **バンドル内訳の実測**（`rollup-plugin-visualizer`）と、その結果の記録。
2. **ルート単位の遅延読み込み**（TanStack Router の `lazyRouteComponent`）。対象は
   `@knowledge` の画面 11 本。**合成点（`featureRegistry` / `createKnowledgeRoutes`）の
   型付きルート構成は壊さない**（[[IADR-0124]] 決定 1）。
3. **初期チャンクに残すものの明示と機械的な固定**（共通シェル・認証・`@platform/ui` のプリミティブ）。
4. **`build.rollupOptions.output.manualChunks` の併用**（実測に基づく。§計測・[[IADR-0134]]）。
5. 分割境界の**回帰ガード**（単体テスト 2 本 ＋ E2E 1 本）。
6. 分割の境界と根拠を **[[IADR-0134]]** に記録する。

### 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **i18n カタログのロケール別遅延読み込み**（ja / en の両方を初期チャンクへ載せている。実測 25.89 kB rendered） | 本書 §申し送り | Lingui の初期化（`initI18n`）と カタログの機械検査（[[IADR-0125]] 決定 4）に触れる。本 issue の射程は「ルート単位」である |
| **`oidc-client-ts` の遅延化**（実測 121.20 kB rendered = 初期チャンク第 3 位） | 第 3 段（#439。ADR-0032 の BFF セッション方式） | issue #512 が「認証は初期チャンクに残す」と明示。**移行第 3 段でライブラリごと消える見込み**であり、いま遅延化しても捨てる作業になる |
| **AST（`src/ai-stock-trading`）3 画面の遅延化**（実測 62.09 kB rendered） | 本書 §申し送り | 旧契約（`FeatureModule.routes[].element`）は**モジュール初期化時に React 要素を作る**ため、本リポ側だけでは遅延化できない。AST は変更できない（[[IADR-0120]]） |
| **`sonner` の遅延化**（実測 64.23 kB rendered） | 本書 §申し送り | 共通シェル（`Layout` が `<Toaster/>` を置く）の一部であり、issue の「共通シェルは初期チャンクに残す」に当たる |
| **画面の内容・文言・権限の変更** | — | 本 issue は分割だけを行う |
| **`chunkSizeWarningLimit` の引き上げ** | — | 警告を消すだけで何も速くならない。issue が求めているのは分割である |

## 設計

### 1. ルートを遅延させる（`lazyRouteComponent`）

`@knowledge` の各 feature の `index.tsx` で、画面モジュールの静的 import を
`lazyRouteComponent(() => import('./XxxPage'), 'XxxPage')` に置き換える。
**ルート factory の形（`(shell: ShellRoute) => Route`）・パス・`validateSearch` は変えない**ため、
[[IADR-0124]] 決定 1 の型付きタプル合成と `<Link to>` / `useSearch({ from })` の型安全は無傷である
（`component` の型はルート ID・パス・検索パラメータの推論に関与しない。§検証で `tsc` により確認した）。

**ガードのある画面（SC-05 / 06 / 07 / 09 / 10 / 11）は形が違う。**

| | ガードなし（SC-01 / 02 / 03 / 04 / 08） | ガードあり（SC-05 / 06 / 07 / 09 / 10 / 11） |
| --- | --- | --- |
| `component` | `lazyRouteComponent(...)` そのもの | `RequireRole` で包む素の関数（初期チャンク） |
| 画面本体 | 同上 | 関数の中で描画される `lazyRouteComponent(...)` |
| 事前読み込み | `router.load()` が `.preload()` を呼ぶ（描画時に suspend しない） | **効かない**（`preloadRouteComponents` は `route.options.component.preload` しか見ない） |
| Suspense 境界 | 不要 | **`wrapInSuspense: true` が要る** |

**ガードを初期チャンク側に残すのは意図的である**——`RequireRole` が先に評価されるため、
権限外の利用者は画面チャンクを**取りに行かない**。`wrapInSuspense` を省くと suspend が
ルート木の最上位まで遡り（`Match.js` の `rootRouteId` の Suspense が受ける）、
**共通シェルごと空白になる**。理由と棄却案は [[IADR-0134]] 決定 2 を正とする。

### 2. 初期チャンクに残すもの（`manualChunks`）

issue #512 の指定どおり、**共通シェル・認証・`@platform/ui` のプリミティブ**は初期ロードに残す。
`@platform/ui` は放置すると Rollup が「2 つ以上の遅延チャンクが共有するモジュール」として
1 kB 未満のチャンクへ切り出す（実測: Label / Tag / Card / Input / Select / StatusBadge）ため、
`manualChunks` で 1 本に束ねる。同じ理由で TanStack Query の内部も束ねる。
React ランタイムは別建てにする（理由は [[IADR-0134]] 決定 3）。

```text
初期ロード（index.html の <script> ＋ modulepreload）
  index          … アプリ本体・foundation・ルータ・i18n カタログ・AST（旧契約）
  vendor-react   … react / react-dom / scheduler / use-sync-external-store
  ui             … @platform/ui のプリミティブと、それが引き込む依存（Radix・tailwind-merge）
  vendor-query   … @tanstack/react-query / query-core
遅延（ルート単位）
  画面 11 本ぶんのチャンク ＋ 画面間で共有する 2 本（apiErrors / confidentiality）
```

### 3. 計測を再現可能にする

`rollup-plugin-visualizer` を `@platform/frontend` の devDependency として入れ、
**環境変数で明示したときだけ**プラグインへ載せる（`pnpm --filter @platform/frontend run build:analyze`
＝ `ANALYZE_BUNDLE=1`）。既定のビルドは成果物も所要時間も変えない。
出力 `dist/stats.json` は生成物であり `src/.gitignore` の `dist` に含まれるためコミットされない
（`.gitignore` への追記は不要だった。実測: `git check-ignore -v src/platform/frontend/dist/stats.json`）。

**依存の増分**: `rollup-plugin-visualizer@7.0.1` を入れると lockfile に推移的依存が加わる
（`cliui` / `emoji-regex` / `is-in-ssh` / `open` / `powershell-utils` ほか。`git diff --stat src/pnpm-lock.yaml` = 113 行の追加）。
**`open`（ブラウザ起動）系が入るのは、既定テンプレートが HTML を開く機能を持つためである**——
本リポでは `template: 'raw-data'` しか使わない。開発時のみの依存であり成果物には入らないが、
**サプライチェーンの面数は増える**ので、値が要らなくなった時点で外す判断はあり得る。

## 受け入れ基準

issue #512 §受け入れ基準 を検証可能な形へ展開する。

- [x] **Vite の 500 kB 警告が出ない。**（§検証。**警告は stderr に出る**ので `2>&1` で捕まえる）
- [x] **初期チャンクのサイズと分割境界の根拠が [[IADR-0134]]・本書に記録されている。**
      根拠は**計測値に紐づく**こと（「重そうだから」で切らない）。
- [x] **分割前後の実測値を並べて記録した**（初期チャンク・遅延チャンクそれぞれ・gzip 込み）。
- [x] **合成点の型付きルート構成を壊していない**（[[IADR-0124]] 決定 1。`pnpm run typecheck` が green で、
      `createKnowledgeRoutes` の戻り値へ型注釈を足していない）。
- [x] **共通シェル・認証・`@platform/ui` のプリミティブが初期チャンクに残っている**
      （目視ではなく `initialChunk.test.ts` が固定する）。
- [x] **存在秘匿の markup 一致テストが引き続き通る**（`Layout.test.tsx`。SC-11 は遅延境界の向こう側にある）。
- [x] `typecheck` / `lint` / `test` / `test:coverage` / `build` / E2E が green。
- [x] `node scripts/check-static-egress.js --require src/platform/frontend/dist` が**分割後の全チャンク**に対して green。
- [x] **変異試験**（壊すと落ちることの実測）を行い、結果を表で残す。**素通りしたものも書く。**

## テスト方針

| 層 | 追加/変更したもの | 見るもの |
| --- | --- | --- |
| 単体（knowledge） | `features/routeSplitting.test.ts`（新規） | 画面 11 本が **feature index の静的 import に戻っていない**こと・遅延境界の宣言（`.preload` / `wrapInSuspense`） |
| 単体（platform） | `foundation/routing/initialChunk.test.ts`（新規） | 共通シェル・`NotFound`・認証ガード・`AuthProvider`・`@platform/ui` が**初期側に残っている**こと |
| 単体（platform） | `foundation/ui/Layout.test.tsx`（改訂） | 存在秘匿の markup 一致の**比較範囲を Outlet の器まで広げた**（§変異試験 M4c） |
| 単体（knowledge） | SC-10 / SC-11 / opsFlow のテスト（改訂） | 遅延で 1 tick 遅れる値の取得を `findBy*` で待つ |
| テスト基盤 | `platform/frontend/src/test/setup.ts`（改訂） | Testing Library の `asyncUtilTimeout` を 1000 → 5000 ms（理由は §遅延境界が持ち込んだテストの揺れ） |
| E2E | `e2e/bundle-splitting.smoke.spec.ts`（新規） | **実ブラウザ**で分割成果物から起動できること・要求した資産がすべて 200 であること |

**検出のしかた（2 本の新規テストに共通）**: `vi.mock` の factory は**実際に import されたときにだけ
評価される**。この性質を使い、`routeSplitting.test.ts` は「読まれ**ない**こと」、
`initialChunk.test.ts` は「読まれること」を固定する。どちらもバンドラを起動せずに
**モジュールグラフの向き**を見ており、CI（`frontend-tests.yml` の Vitest）で毎回走る。

## 計測（実測。**推測で分割しないための一次資料**）

**測定条件**: worktree `perf/NFR-spa-route-code-splitting`（`origin/develop` `68d91ce` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vite 6.4.3 ／ `rollup-plugin-visualizer` 7.0.1 ／
submodule `planning`（pin `d980a01`）と `src/ai-stock-trading`（pin `655e2ed`）は populate 済み。

### 分割前（`origin/develop` `68d91ce` そのまま。`pnpm run build` の生の出力）

```text
vite v6.4.3 building for production...
transforming...
✓ 2019 modules transformed.
rendering chunks...
computing gzip size...
dist/index.html                   0.69 kB │ gzip:   0.51 kB
dist/assets/index-zRuMIu0a.css    7.73 kB │ gzip:   2.50 kB
dist/assets/index-Bw-dS6vy.js   632.98 kB │ gzip: 190.04 kB

(!) Some chunks are larger than 500 kB after minification. Consider:
- Using dynamic import() to code-split the application
- Use build.rollupOptions.output.manualChunks to improve chunking: https://rollupjs.org/configuration-options/#output-manualchunks
- Adjust chunk size limit for this warning via build.chunkSizeWarningLimit.
✓ built in 5.37s
```

### 初期チャンクの内訳（visualizer。**minify 前の rendered bytes**）

632.98 kB（minify 後）に対応する rendered は **1527.89 kB** である。以下の比率はこの 1527.89 kB に対する。
分類は「`node_modules` のパッケージ名」「`knowledge` の feature ディレクトリ」「AST」「`packages/ui`」
「`platform/frontend/src`」で機械的に集計した（`dist/stats.json` を集計するスクリプトを scratch で使用）。

| 区分 | rendered | 比率 | 内訳（上位） |
| --- | --- | --- | --- |
| **依存（node_modules）** | **1253.27 kB** | **82.0%** | react-dom 561.39 ／ @tanstack/router-core 131.65 ／ **oidc-client-ts 121.18** ／ **tailwind-merge 102.19** ／ @tanstack/query-core 75.62 ／ **sonner 64.23** ／ @tanstack/react-router 42.49 ／ react 20.31 ／ @radix-ui/* 計 約 45 ／ scheduler 11.42 ／ @tanstack/store 11.02 ／ @lingui/core 10.91 ／ @tanstack/history 10.51 ／ lucide-react 5.72 |
| 画面（knowledge の 11 feature） | 146.98 kB | 9.6% | sc09 30.07 ／ sc11 23.84 ／ sc05 16.53 ／ sc06 14.36 ／ sc10 12.10 ／ sc01 10.70 ／ sc03 10.54 ／ sc07 9.96 ／ sc08 9.61 ／ sc02 6.68 ／ sc04 1.09 |
| AST（旧契約ユニット） | 62.09 kB | 4.1% | — |
| アプリ（platform/frontend/src） | 53.38 kB | 3.5% | **i18n カタログ ja＋en 25.89** ／ foundation 25.41 ／ orval 生成 1.01 |
| `@platform/ui` のソース | 10.76 kB | 0.7% | — |
| その他 | 1.42 kB | 0.1% | — |

**この表が本作業の方針を決めた。**

1. **画面コードは全部で 9.6% しかない。** ルート境界で動かせる上限がこれである
   （＋ AST 4.1% は本リポからは動かせない）。
2. **重いのは依存であり、しかも「1 つの巨大な依存」ではない**——react-dom（36.7%）を除くと、
   100 kB 級が 3 つ（router-core / oidc-client-ts / tailwind-merge）並ぶ。
3. **`tailwind-merge` が 102.19 kB ある**のは想定外だった（`cn()` の実装のためだけに入っている）。
   推測で分割していれば見落としていた枠である。

### 分割後（採用構成。`pnpm run build` の生の出力）

```text
vite v6.4.3 building for production...
transforming...
✓ 2020 modules transformed.
rendering chunks...
computing gzip size...
dist/index.html                                     0.93 kB │ gzip:  0.57 kB
dist/assets/ui-DsapRUmu.css                         7.76 kB │ gzip:  2.51 kB
dist/assets/confidentiality-BLPf81Cd.js             0.12 kB │ gzip:  0.11 kB
dist/assets/apiErrors-Br-ZWezi.js                   0.17 kB │ gzip:  0.15 kB
dist/assets/WikiAccessPage-DGgJk8Gg.js              0.74 kB │ gzip:  0.54 kB
dist/assets/SearchResultsPage-CCzGYp0f.js           2.86 kB │ gzip:  1.30 kB
dist/assets/ConversionJobsPage-BMP2YYXF.js          4.00 kB │ gzip:  1.65 kB
dist/assets/AnalysisDashboardPage-CyxMoTej.js       4.66 kB │ gzip:  1.95 kB
dist/assets/SearchChatPage-KKDGZBbX.js              4.86 kB │ gzip:  1.89 kB
dist/assets/DocumentDetailPage-BgkRtfdB.js          5.10 kB │ gzip:  1.85 kB
dist/assets/OperationsDashboardPage-D5qiPsAA.js     5.20 kB │ gzip:  1.90 kB
dist/assets/DataSourceManagementPage-DWO7U0G_.js    5.89 kB │ gzip:  2.23 kB
dist/assets/DocumentManagementPage-DMBpzJog.js      6.76 kB │ gzip:  2.54 kB
dist/assets/ConfigViewerPage-CMuUSlYe.js            9.38 kB │ gzip:  2.84 kB
dist/assets/AdminAbacSettingsPage-BcKBdV_J.js      11.78 kB │ gzip:  3.56 kB
dist/assets/vendor-query-9WzLNTk0.js               41.48 kB │ gzip: 12.33 kB
dist/assets/ui-BeFdQHLQ.js                         65.04 kB │ gzip: 20.43 kB
dist/assets/vendor-react-CHRHn5b-.js              196.69 kB │ gzip: 61.67 kB
dist/assets/index-GYWu_vx3.js                     274.33 kB │ gzip: 83.51 kB
✓ built in 5.60s
```

**500 kB 警告は出ない**（この出力は `2>&1` で標準エラーを含めて取得している。**警告は stderr に出る**ため、
stdout だけを見ていると「警告が消えた」と誤って判定する——実際に一度誤判定した。§変異試験 の前提）。

### 前後の対比

| | 分割前 | 分割後 | 差 |
| --- | --- | --- | --- |
| **初期ロードの JS 合計** | **632.98 kB**（1 本） | **577.54 kB**（4 本） | **−55.44 kB（−8.8%）** |
| **同 gzip** | **190.04 kB** | **177.94 kB** | **−12.10 kB（−6.4%）** |
| 最大チャンク | 632.98 kB | **274.33 kB** | −358.65 kB |
| 遅延チャンク合計 | — | 61.52 kB（gzip 22.51 kB。13 本） | — |
| JS 総量（初期＋遅延） | 632.98 kB | 639.06 kB | **+6.08 kB**（分割の境界コスト） |
| CSS | 7.73 kB / gzip 2.50 kB | 7.76 kB / gzip 2.51 kB | ほぼ同一（帰属チャンクが `index` → `ui` へ移り名前が変わる） |
| 500 kB 警告 | **あり** | **なし** | — |

**初回訪問で減るのは 12.10 kB（gzip）にとどまる。** 初期チャンクの 82% が依存であり、
ルート境界では動かせないためである（§計測 の 1）。**より大きい効果は再訪時にある**——
`vendor-react`（196.69 kB / gzip 61.67 kB）はアプリ側の更新でハッシュが変わらず、
キャッシュが効き続ける。この 2 つを混同しないことが本作業の記録上いちばん重要な点である。

### 分割方針の候補（全部ビルドして測った）

`origin/develop` 基点のビルドを V0、ルート単位の遅延だけを入れたものを V1 とし、
そこへ `manualChunks` の規則を足し引きした 5 通りを実測した。すべて**同一のソース状態**で測っている。

| 変種 | 最大チャンク | 初期ロード JS | 同 gzip | 500 kB 警告 | JS チャンク数 | 1 kB 未満の遅延チャンク |
| --- | --- | --- | --- | --- | --- | --- |
| V0 分割なし（`68d91ce`） | 632.98 kB | 632.98 kB | 190.04 kB | **あり** | 1 | 0 |
| V1 ルート遅延のみ | 533.66 kB | 533.66 kB | 164.33 kB | **あり** | 23 | 9 |
| V2 ＋`vendor-react` のみ | 335.72 kB | 532.41 kB | **163.63 kB** | なし | 24 | 9 |
| V3 ＋`ui` のみ | 487.98 kB | 566.02 kB | 175.11 kB | なし | 17 | 3 |
| V4 ＋`vendor-query` のみ | 495.19 kB | 545.86 kB | 168.52 kB | なし | 23 | 9 |
| V6 全依存を単一 `vendor` へ | 494.74 kB | 573.54 kB | 176.60 kB | なし | 23 | 10 |
| **V5 採用（3 規則）** | **274.33 kB** | 577.54 kB | 177.94 kB | なし | 17 | 3 |

読み取れること（**採用の根拠であり、[[IADR-0134]] 決定 3 の一次資料**）:

- **V1 では警告が消えない。** ルート単位の遅延だけでは 533.66 kB までしか下がらない
  （＝ issue が想定した「ルート分割で警告が消える」は**成り立たなかった**）。
- **V6（全依存を 1 本の vendor へ）は 494.74 kB で、上限の 1.05% 手前でしかない。**
  依存を 1 つ足せば警告が戻る。issue が警告していた「安易な vendor 一括」は実測でも筋が悪い。
  V4 も同様（495.19 kB。上限の 0.96% 手前）。
- **初期ロードの gzip が最小なのは V2（163.63 kB）である。** V5 はそれより **14.31 kB 重い**。
  差の中身は「`@platform/ui` のプリミティブとその依存（Radix ほか）を初期側に置く」ぶんである。
  issue #512 が「`@platform/ui` のプリミティブは初期チャンクに残す」と明示しているため V5 を採る。
  **数字は残しておく**——この 14.31 kB を惜しむなら V2 へ倒す判断があり得る（§申し送り）。
- V5 と V3 は 1 kB 未満の遅延チャンクが同数（3 本）で構成が近いが、**上限までの余裕が大きく違う**
  （274.33 kB vs 487.98 kB）。V5 は +2.83 kB gzip で 213.65 kB ぶんの余裕を買っている。

## 検証（実測）

**測定条件は §計測 と同じ。** スコープは断りがない限りワークスペース全体（`src/` の 4 パッケージ ＋ AST）。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components` で、本作業の着手前と同数） |
| 単体テスト | `pnpm run test` / `pnpm run test:coverage` | **59 files / 557 tests** 全 green（`test:coverage` は **8 回連続**で green を確認。理由は §遅延境界が持ち込んだテストの揺れ）（本作業前は **57 files / 539 tests**。差は新規 2 ファイル ＝ `routeSplitting.test.ts` 12 件 ＋ `initialChunk.test.ts` 6 件） |
| ビルド | `pnpm run build` | green・**500 kB 警告なし**（§計測 に生の出力） |
| E2E | `playwright test`（後述の条件） | **13 tests 全 green**（本作業で 1 本追加＝`bundle-splitting.smoke.spec.ts`） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（**20 ファイル**・検出 0 件。分割前は 4 ファイルだった） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- …/generated` | green（差分なし） |
| i18n カタログ | `pnpm run i18n` ＋ `git diff --exit-code -- …/locales`／`node scripts/check-i18n-catalogs.js` | green（差分なし／2 ロケール・未翻訳 0 件） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件中 28 件が写像済み。**allowlist は着手前と同じ 7 件**＝増やしていない） |
| テスト仕様書の被覆 | `node scripts/check-test-spec-coverage.js` | green（床と一致。**本作業はフロントのテストのみを足しており、本検査の対象＝バックエンドの `*Tests.cs` は増減していない**） |
| scripts の自己検査 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（247 tests） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数はここに書かない**——この表を直すコミット自身が件数を変えるため。最終形は CI の `commit-messages` ジョブが検査する） |

**単体テストの件数の取り方**: 着手前の値は `git stash -u` で本作業の変更を退避した状態の
`pnpm run test` を実走して得た（**57 files / 539 tests**）。本作業後は **59 files / 557 tests** で、
差は新規 2 ファイル（`routeSplitting.test.ts` 12 件 ＋ `initialChunk.test.ts` 6 件）と一致する。
**作業途中の中間値（58 files / 551 tests）を最終値として書きかけた**——中間値は
`initialChunk.test.ts` を足す前のものであり、最終形とは合わない。**件数は最終状態で測り直すこと。**

### カバレッジ

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（本 PR） | **96.25%**（5471/5684） | **90.01%**（1135/1261） | **91.83%**（416/453） |
| MSP 所有分（本 PR） | **95.65%**（4204/4395） | **90.99%**（868/954） | **93.08%**（323/347） |
| （参考）着手前 `68d91ce` の MSP 所有分 | 95.24% | 90.69% | 93.08% |
| 床（`src/vitest.config.ts`） | 90 | 85 | 88 |

MSP 所有分は `src/coverage/lcov.info` から `ai-stock-trading` のファイルを除いて再集計した値である
（`LF/LH`・`BRF/BRH`・`FNF/FNH` を全ファイルで合算）。**床は据え置く**——既存の導出規則
（実測から 5pt 下・切り捨て）を当てると lines 90 / branches 85 / functions 88 となり、現行の床と同値である。
`coverage.exclude` は増やしていない。

### E2E の実行条件

**この環境では `playwright install` がブラウザを取得できない**ため、インストール済みの
`/opt/pw-browsers/chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、**確認後に削除した**（#490 / #496 / #502 / #503 / #504 と同じ作法）。
**リポジトリの `playwright.config.ts` は無改変である。**

追加した `bundle-splitting.smoke.spec.ts` が見るのは**初期ロードの健全性**に限る
（要求した資産がすべて 200・`pageerror` なし・`/assets/*.js` を 2 本以上読む・ログイン画面が描画される）。
**認証済みの遅延ルートはこの層では実走できない**——トークンは `InMemoryWebStorage` に保持され
外部から注入できないためで、#504 が記録した制約と同じである。遅延ルートの描画そのものは
Vitest 側の各画面テストが**実際に動的 import を通して**固定している。

### 遅延境界が持ち込んだテストの揺れ（**実測して直した**）

2 種類の揺れが出た。**どちらも「遅延で画面の mount が後ろへずれる」ことが原因**である。

| # | 症状 | 原因 | 直し方 |
| --- | --- | --- | --- |
| 1 | SC-10 / SC-11 / opsFlow の 3 か所で、見出しを `findBy*` で待った直後の `getByText` が値を見つけられない | 見出しは画面の静形、値は `useQuery` の解決後。mount が 1 tick 遅れたぶん取得の解決も後ろへずれた | 値の側も `findBy*` で待つ |
| 2 | `sc05-documents` の一覧テストが **`pnpm run test:coverage` でのみ 9 回中 1 回落ちる**（`Unable to find role="link"`。`pnpm run test` では再現しない） | ガード配下の画面は `router.load()` の事前読み込みが効かず、**`findBy*` の待ち時間に動的 import が乗る**。カバレッジ計測を有効にすると既定の 1000 ms に収まらないことがある | `asyncUtilTimeout` を 5000 ms へ（`platform/frontend/src/test/setup.ts`） |

**2 は本作業が持ち込んだ揺れである**（着手前の `68d91ce` は `pnpm run test:coverage` を **6 回中 6 回** green）。
是正後は **8 回中 8 回** green を確認した（所要は 39〜41 秒で、是正前と変わらない——
`waitFor` のポーリングは条件成立で止まるため、上限を延ばしても通るテストは遅くならない）。
**「たまたま緑だった」を最終確認にしない**ために、`test:coverage` を繰り返し実走して確かめている。

## 変異試験（「壊すと落ちる」ことの実測）

**件数の基準**: 下表の **M1〜M8 の 8 件**が変異であり、**M4a〜M4c は M4 の 3 変種**として個別に数える
（合計 10 行）。実行は「変異を当てる → 該当テストだけ走らせる → 必ず復元する」で行った。
**素通りしたものも隠さず載せる。**

| # | 壊した箇所 | 落ちたもの |
| --- | --- | --- |
| M1 | SC-09 の `lazyRouteComponent` を静的 import へ戻す（＝画面が初期チャンクへ帰る） | `routeSplitting.test.ts` **2 件**（`keeps every screen module out of the eagerly imported graph` / `SC-09 is reachable only through a lazy boundary`） |
| M2 | SC-01 の遅延 import を存在しないモジュール（`./NoSuchScreenPage`）へ向ける | **`pnpm run typecheck` / `pnpm run build`**（`error TS2307`）。単体テストも 1 ファイルが収集に失敗する |
| M3 | SC-11 の `wrapInSuspense: true` を外す | **`routeSplitting.test.ts` 1 件のみ**。**画面テスト（SC-11 の 17 件）・`Layout.test.tsx` は素通りした**——jsdom でも実ブラウザでも、suspend はルート木最上位の Suspense が受けるため「動きはする」（共通シェルごと空白になるだけ）。**この退行を捕まえる手段は本 PR で足したテストしか無い** |
| M4a | `NotFound` の見出し文言を 1 文字変える | **5 件**（`Layout.test.tsx` 3 / `sc11-config/access.test.tsx` 2） |
| M4b | `NotFound` の `padding` を 1 文字変える（見出しは変えない） | **素通り**。markup 一致テストは**両側を同じ関数で描く**ため、両方が同じだけ変わる変異は原理的に検出しない（意図どおり。検出するのは**乖離**である） |
| M4c | **未知パス側の `NotFound` だけ**を `<div>` で包む（＝存在秘匿の乖離そのもの） | **是正前は素通りした。** 比較範囲が「見出しの親（＝`NotFound` 自身の `<main>`）」に閉じており、包む要素の違いが比較の外に落ちていた。**比較範囲を Outlet の器（共通シェルの `<main>`）まで広げて是正**し、再測定で 1 件が落ちることを確認した |
| M5a | **共通シェル（`Layout`）を遅延側へ移す** | **是正前は完全に素通りした**（`typecheck` green・`pnpm run test` **551 件全 green**・`build` は警告も出さず初期チャンクがむしろ 274.33 → 238.71 kB に縮む）。**`initialChunk.test.ts` を足して是正**し、再測定で 1 件が落ちることを確認した |
| M5d | ガード（`RequireRole`）ごと外して画面を直接ルートに載せる | **4 件**（`sc11-config` の access / 画面テスト） |
| M6 | `manualChunks` の `ui` 規則を外す | **素通り**（ビルドは成功し警告も出ない。1 kB 未満の遅延チャンクが 3 → 9 本に増えるだけ）。**チャンク構成そのものを検査する機械は無い**（§申し送り 3） |
| M7 | `manualChunks` の `vendor-react` 規則を外す | **素通り**（index が 274.33 → 458.79 kB へ増えるが 500 kB は超えないため警告も出ない）。同上 |
| M8 | 分割後の**遅延**チャンク（`ConfigViewerPage-*.js`）へ CDN の URL を混ぜる | **`check-static-egress.js` が落ちる**（`cdn.jsdelivr.net -> cdn.jsdelivr.net`）。**走査が初期チャンクだけを見ている穴は無い**ことを確認した（走査対象は 4 → 20 ファイルへ増えている） |

**素通り 3 件（M4b / M6 / M7）の扱い**:

- **M4b は仕様どおり**（乖離を見るテストであって変更を見るテストではない）。
- **M6 / M7 は検出手段が無い。** チャンクの分け方は「ビルドは通る・テストも通る・警告も出ない」
  という三重の意味で静かに壊れる。**本 PR ではこれを検査する機械を足していない**——
  新しい `scripts/check-*.js` を足すと CI への結線（`.github/workflows/`）が要るが、
  本作業の権限では編集できないため、検査が走らないまま増えることになる。§申し送り 3 で起票を求める。

## 申し送り

1. **i18n カタログのロケール別遅延読み込み**（初期チャンクに ja＋en の両方が載っている。実測 25.89 kB rendered）。
   実行時に使うのは片方だけであり、`initI18n` を動的 import へ変えれば初期ロードから落とせる。
   [[IADR-0125]] 決定 3・4（カタログのコミットと再生成差分検査）に触れるため別 issue が要る。
2. **AST 3 画面（62.09 kB rendered）の遅延化**。旧契約（`FeatureModule.routes[].element`）が
   モジュール初期化時に React 要素を作るため、本リポ側だけでは遅延化できない。
   [[IADR-0124]] 決定 2 が既に挙げている「AST を新契約（型付き factory）へ移す」が済めば、
   ルート単位の遅延がそのまま適用できる。**AST リポジトリ側の issue が要る**。
3. **チャンク構成の機械検査**（変異試験 M6 / M7 が素通りした穴）。
   「1 チャンクが 500 kB を超えない」「初期ロードの合計が ratchet を割らない」を
   `scripts/check-*.js` として足し、`frontend.yml` へ結線したい。**本作業では `.github/workflows/` を
   編集できないため見送った**（結線できない検査を足すと「あるのに走らない」状態を作る）。
4. **`@platform/ui` を初期チャンクに置く判断の再考余地**（§計測 の V2 と V5 の差 = **gzip 14.31 kB**）。
   差の実体は `Tabs` が引き込む Radix である（`Tabs` を使う画面は SC-09 だけ）。
   `@platform/ui` の公開面は `src/index.ts` の 1 ファイルと決まっている（[[IADR-0125]] 決定 1）ため、
   barrel が初期側にある限り Radix も初期側に来る。**規約を変えずに直す方法は無い**。
5. **`tailwind-merge`（102.19 kB rendered）の妥当性**。`cn()` のためだけに入っており、
   初期チャンクの依存の中で react-dom / router-core / oidc-client-ts に次ぐ 4 位である。
   置き換え候補の評価は本 issue の射程外。
6. **IADR の採番**: 本 PR は **IADR-0134** を採った。基点 `68d91ce` には `IADR-0131` が無いが、
   `develop`（`727d021`）に `IADR-0131`（マージ済み）が、並行作業のブランチ
   `fix/NFR-openapi-response-required` に `IADR-0132`（未マージ）が存在するためである。
   **`IADR-0132` の PR が先にマージされない場合、本 PR は `IADR-0132` へ改番する**
   （欠番を作らない。改番手順は `.claude/rules/traceability.md` §採番衝突時の改番手順）。
