import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

import js from '@eslint/js';
import globals from 'globals';
import importPlugin from 'eslint-plugin-import';
import lingui from 'eslint-plugin-lingui';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import storybook from 'eslint-plugin-storybook';
import tanstackQuery from '@tanstack/eslint-plugin-query';
import tanstackRouter from '@tanstack/eslint-plugin-router';
import testingLibrary from 'eslint-plugin-testing-library';
import tseslint from 'typescript-eslint';
import ts from 'typescript';

// ADR-0031 / IADR-0121 決定 8: 新スタックの禁止事項を機械強制する。専用の検査スクリプトを作らないのは、
// 対象が import と識別子の静的検査＝ESLint の守備範囲そのものであり、検査器を増やすほど
// 「走らせ忘れ」と二重メンテが増えるためである。
// 各ユニットのブロックが no-restricted-imports を上書きしてしまうため（flat config は同一ルールを
// 後勝ちで置換する）、共通パターンは定数にして各ブロックへ必ず展開する。
// `export` しているのは `eslint.templates.config.js`（雛形用）が同じ配列を使い回すためである。
// **雛形へ別立ての禁止リストを書かない**——2 本になった時点で必ず片方が古くなる。
export const BANNED_IMPORT_PATTERNS = [
  {
    // 13_frontend-stack: クライアント状態は Zustand、サーバー状態は TanStack Query。
    // グローバルストア（Redux）は持たない。
    group: ['redux', 'react-redux', '@reduxjs/*', 'redux-*'],
    message:
      'Redux は不採用（ADR-0031）。サーバー状態は TanStack Query、クライアント状態は Zustand を使う。',
  },
  {
    // 13_frontend-stack §基本方針:「BFF の OpenAPI から orval で生成する（手書きクライアント禁止）」。
    group: ['axios', 'ky', 'superagent', 'got', 'node-fetch', 'openapi-fetch'],
    message:
      '手書きの HTTP クライアントは禁止（ADR-0031）。BFF 呼び出しは orval 生成フックを使う。' +
      // ［2026-08-08 / フェーズ末クロス監査］従前ここは「または @foundation/api の apiFetch / apiStream を使う」
      // と案内していたが、**画面（features）では apiFetch は別の規則が error にする**（IADR-0146）。
      // 案内どおりに直すと別の error になるため、どこから呼ぶかで割って書く。
      'SSE だけは apiStream が恒久的な正規の口（IADR-0131 決定 4）。' +
      'apiFetch は foundation 配下でのみ使ってよい——画面からは禁止（IADR-0146）。',
  },
  {
    // IADR-0121 決定 4: 共有 UI の公開面は @platform/ui のエントリのみ。
    group: ['@platform/ui/src', '@platform/ui/src/*'],
    message:
      '@platform/ui の内部実装を直接参照しない。公開面は "@platform/ui" と "@platform/ui/styles.css" のみ。',
  },
];

// NFR / #555 / IADR-0146: 画面（features）からの `apiFetch` 再混入を止める。
//
// **なぜ必要か**: #519 が画面の通信を orval 生成物へ載せ替え、`apiFetch` の呼び出しは
// **本番コードから 0 件**になった。これにより契約の変更が型検査で捕まるようになったが、
// **その状態を守る仕組みが無かった** —— ESLint は `fetch` / `axios` を止めるが、
// **`apiFetch` は `foundation/api` の正規 API なので止まらない**（IADR-0121 決定 3）。
// 次の実装者が `apiFetch` ＋ 手書き型で書いても **CI は緑**であり、その画面ぶんだけ
// 「契約を変えても型検査が落ちない」状態が静かに戻る（#512 の M5a と同じ「壊れても何も赤くならない」型）。
//
// **`apiStream` は禁止しない。** SSE は orval が扱えず生成物が存在しないため
// （IADR-0131 決定 4）、`foundation/api` の `apiStream` が**恒久的に正規の口**である。
// 実際 `knowledge/frontend/src/features/sc01-search/useAskStream.ts` が唯一の利用箇所である。
// **禁止の対象を `apiFetch` に限ることが、そのまま例外の明示になっている**
// ——「SSE だけ許可リストに載せる」形にすると、許可リストの保守という新しい手作業が増える。
// `export` の理由は BANNED_IMPORT_PATTERNS と同じ（雛形用 config が使い回す）。
export const NO_APIFETCH_IN_FEATURES = {
  name: '@foundation/api/apiClient',
  importNames: ['apiFetch'],
  message:
    '画面（features）から apiFetch を呼ばない（#555 / IADR-0146）。BFF 呼び出しは orval 生成フックを使う' +
    '——apiFetch は手書き型と組で使われるため、その画面だけ契約変更が型検査で捕まらなくなる。' +
    'SSE は apiStream が恒久的な正規の口（IADR-0131 決定 4）。',
};

// ［2026-08-08 / フェーズ末クロス監査］`bffFetch` も塞ぐ。
// `apiFetch` だけを禁じても、**同じ「任意 URL ＋ 手書き型」の口がもう 1 つ空いていた** ——
// `orvalMutator.bffFetch<T>(url, options)` は orval 生成物が使う mutator だが `export` されており、
// features から import すれば `apiFetch` と同じ抜け道になる（IADR-0146 決定 2 が「禁止対象を絞ること
// 自体が例外の明示」と述べながら、`bffFetch` を「検出しないこと」へ挙げていなかった＝片側しか書いていない）。
// **実測では features からの import は 0 件**（利用は `foundation/api/generated/` の 4 ファイルのみ）なので、
// 禁止しても既存コードは壊れない。**生成物は features ではないため対象外である。**
// `export` の理由は BANNED_IMPORT_PATTERNS と同じ（雛形用 config が使い回す）。
export const NO_BFFFETCH_IN_FEATURES = {
  name: '@foundation/api/orvalMutator',
  importNames: ['bffFetch'],
  message:
    '画面（features）から bffFetch を呼ばない（#555 / IADR-0146）。bffFetch は orval 生成物の mutator であり、' +
    '直接呼ぶと apiFetch と同じく「任意 URL ＋ 手書き型」になって契約変更が型検査で捕まらなくなる。' +
    'BFF 呼び出しは orval 生成フックを使う。',
};

// ADR-0031 / IADR-0124: ルーティングは TanStack Router に一本化する（移行第 2 段 / #490）。
// 「各系統は 1 度だけ切り替え、2 つのルータが同時に存在する状態を作らない」（IADR-0121 決定 1）を
// 機械で守る。本リポジトリが所有する platform / knowledge にのみ適用する——`ai-stock-trading` は
// 別プロジェクトの submodule（IADR-0120）であり、本リポの規約を及ぼさない（旧契約ブリッジで動く）。
// `patterns` ではなく `paths`（完全一致）で指定する——`patterns` は matchBase で照合するため、
// `react-router` は `@tanstack/react-router` にも当たってしまう（実測）。
// `export` の理由は BANNED_IMPORT_PATTERNS と同じ（雛形用 config が使い回す）。
export const NO_LEGACY_ROUTER_PATHS = ['react-router', 'react-router-dom'].map((name) => ({
  name,
  message:
    'react-router は不採用（ADR-0031）。ルーティングは @tanstack/react-router を使う（IADR-0124）。',
}));

// ADR-0066 決定 1〜3 / IADR-0308 / issue #1065: **feature 境界**の機械強制。
//
// **既存の規則が守っているのはユニット境界であって feature 境界ではない**（ADR-0066 の実測）。
// `@foundation` / `@features` / `@knowledge` の禁止はユニットをまたぐ参照を止めるが、
// **同一ユニット内の feature どうしは素通りする**。実際 #1065 の時点で 7 ファイルが素通りしていた。
//
// 🔴 **zones を手書きの許可リストにしない。** `features/` を実際に読んで生成する。
// ADR-0066 §理由 が「許可リストの保守が人に戻ると伸ばし忘れが規則の穴になる」と述べており、
// 本ファイルには既にその形（lingui の `files` を画面のたびに伸ばす運用）が在る。同じ形を増やさない。
// **画面を足しても、このファイルは触らなくてよい。**
const CONFIG_DIR = import.meta.dirname;

/**
 * tsconfig の `paths` から「エイリアス名 → 実体の絶対パス」を組む。
 *
 * 🔴 **エイリアスの正本を増やさないための関数である。** 向き先は
 * `platform/frontend/tsconfig.app.json` / `platform/frontend/vite.config.ts` / `src/vitest.config.ts`
 * の 3 箇所に在る（README がそう定めている）。**ここへ 4 つ目の表を書かない** ——
 * 書いた瞬間に「lint だけ古い向き先で緑」という壊れ方が生まれる。
 *
 * JSONC（`//` コメント入り）を読むために `typescript` の `readConfigFile` を使う。
 * すでに devDependency であり、同じファイルを型検査でも読んでいる。
 */
const tsconfigAliases = (tsconfigRel) => {
  const file = path.join(CONFIG_DIR, tsconfigRel);
  const { config, error } = ts.readConfigFile(file, (p) => readFileSync(p, 'utf8'));
  if (error) throw new Error(`tsconfig を読めない: ${tsconfigRel}`);
  const options = config.compilerOptions ?? {};
  const baseDir = path.resolve(path.dirname(file), options.baseUrl ?? '.');
  const aliases = {};
  for (const [pattern, targets] of Object.entries(options.paths ?? {})) {
    // `@foundation/config` と `@foundation/config/*` は同じ向き先なので、末尾の `/*` を落として畳む。
    aliases[pattern.replace(/\/\*$/, '')] = path.resolve(baseDir, targets[0].replace(/\/\*$/, ''));
  }
  return aliases;
};

/** 本リポジトリ最小の path エイリアス リゾルバ（`eslint-import-resolver-unit-alias.cjs` を参照）。 */
const UNIT_ALIAS_RESOLVER = path.join(CONFIG_DIR, 'eslint-import-resolver-unit-alias.cjs');

/**
 * ADR-0066 決定 3 / IADR-0308: `import/no-restricted-paths` の解決器設定を 1 ユニットぶん作る。
 *
 * 🔴 **2 つとも要る。** `import/no-restricted-paths` は**解決できた import しか見ない**ので、
 * 片方でも欠けると規則は**静かに 0 件で通る**。
 *   1. node リゾルバの `extensions` —— 既定は `.mjs/.js/.json/.node` だけで、`.ts` / `.tsx` を
 *      1 件も解決しない（IADR-0308 が踏んだ穴）。
 *   2. **エイリアス リゾルバ** —— platform の内部参照は 26 ファイル・59 文が `@foundation/*` で
 *      書かれており、これを解決できないと**規則は platform でほぼ何も守らない**（実測 2026-08-30）。
 */
const resolverSettingsFor = (tsconfigRel) => ({
  'import/resolver': {
    node: { extensions: ['.js', '.jsx', '.ts', '.tsx'] },
    [UNIT_ALIAS_RESOLVER]: { aliases: tsconfigAliases(tsconfigRel) },
  },
});

const PLATFORM_RESOLVER = resolverSettingsFor('platform/frontend/tsconfig.app.json');
const KNOWLEDGE_RESOLVER = resolverSettingsFor('knowledge/frontend/tsconfig.json');

/** `<unitSrcRel>/features/` 直下のディレクトリ名（＝ feature 名）を実ファイルから読む。 */
const featureNamesOf = (unitSrcRel) =>
  readdirSync(path.join(CONFIG_DIR, unitSrcRel, 'features'), { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort();

/**
 * ADR-0067 決定 5 の「shared」層。`app` と `features` を参照してはならない側である。
 *
 * ［2026-08-30 追記 / ADR-0067］**`config` / `assets` / `locales` を足した。**
 * 従前ここは 6 つで、`assets` / `locales` / `testing` を「決定 2 の表が挙げていない」として
 * 除いていた。ADR-0067 §決定 5 は**その欠落こそがゾーンを書き切れなくしていた**と裁定し、
 * `src/` 直下を網羅する 4 層の表へ改めた。`config` が加わるのは決定 1（原典 Bulletproof React が
 * `config` を `app` の兄弟に置いている）による。**`testing` はここに入れない** ——
 * 第 4 の層であり、参照してよい先が shared より広い（下記）。
 */
const SHARED_DIRS = [
  'components',
  'hooks',
  'lib',
  'stores',
  'types',
  'utils',
  'config',
  'assets',
  'locales',
];

/** ADR-0067 決定 5 の「本番コードの 3 層」。`testing` を除いた全体（＝`testing` の被参照禁止の target）。 */
const PRODUCTION_DIRS = [...SHARED_DIRS, 'features', 'app'];

/**
 * 1 ユニットぶんの zones を作る（ADR-0066 決定 1 = feature 間、ADR-0067 決定 5 = 4 層の向き）。
 *
 * **`basePath` を明示するのが要点である。** 既定は `process.cwd()` であり、
 * `eslint` をどこから起こしたかで zone の解決先がずれる（`pnpm run lint` は `src/`、
 * `lint:templates` はリポジトリルート）。設定ファイルの位置に固定する。
 *
 * 🔴 **`target` に glob を書かない。** `import/no-restricted-paths` は glob の `target` を
 * minimatch で照合するが、minimatch はパス区切りに `/` を要求する。Windows の
 * 絶対パス（`\`）とは一致しないため、**CI（Linux）でだけ効いてローカルでは静かに 0 件**という
 * 形になる。「テストファイルだけ除く」は glob ではなく **ESLint の `files` / `ignores`** で表す
 * （`productionOnly` 引数。呼び出し側を参照）。
 *
 * @param unitSrcRel ユニットの `src` へのリポジトリ相対パス
 * @param productionOnly 本番コード限定のゾーン（`testing/` の被参照禁止）を含めるか
 */
const featureIsolationZones = (unitSrcRel, { productionOnly = false } = {}) => [
  // ADR-0066 決定 1: feature どうしを import しない（自分自身だけを except にする）。
  ...featureNamesOf(unitSrcRel).map((name) => ({
    target: `./${unitSrcRel}/features/${name}`,
    from: `./${unitSrcRel}/features`,
    except: [`./${name}`],
    message:
      'feature どうしを import しない（ADR-0066 決定 1）。2 つ以上の feature が要る語彙・部品・型は ' +
      'lib/ か components/ へ出し、feature の組み合わせは app/ で行う。',
  })),
  // ADR-0067 決定 5: shared は features・app を参照しない。
  {
    target: SHARED_DIRS.map((dir) => `./${unitSrcRel}/${dir}`),
    from: [`./${unitSrcRel}/features`, `./${unitSrcRel}/app`],
    message:
      `共有層（${SHARED_DIRS.join(' / ')}）から features・app を参照しない` +
      '（ADR-0067 決定 5。依存の向きは shared → features → app の一方向）。' +
      '実行時 config は shared（config/）、設定済み i18n は shared（lib/i18n/）に在る。',
  },
  // ADR-0067 決定 5: features は app を参照しない。
  // **合成点（platform の features/index.ts）は app 層である**（決定 4）。除外はゾーンではなく
  // ブロックの `ignores` が担う（同じパスを 2 箇所へ書かない）。
  {
    target: `./${unitSrcRel}/features`,
    from: `./${unitSrcRel}/app`,
    message: 'features から app を参照しない（ADR-0067 決定 5。合成点は app 層なので対象外）。',
  },
  // ADR-0067 決定 5: testing（第 4 の層）は shared と app を参照してよいが、features は参照しない。
  // **`app` を許すのが要点である** —— テストユーティリティは実アプリのプロバイダ木
  // （ルータ・i18n）を組み立てるためにアプリケーション層を要る。禁じると「テストが実アプリと
  // 違う木で走る」ことになる（ADR-0067 §決定 5 の理由）。
  {
    target: `./${unitSrcRel}/testing`,
    from: `./${unitSrcRel}/features`,
    message: 'テストユーティリティ（testing/）から features を参照しない（ADR-0067 決定 5）。',
  },
  // ADR-0067 決定 5: `testing/` は参照される側にならない（向きを一方向に保つ代償）。
  // **本番コードにだけ掛ける。** テストがテストユーティリティを引くのは正しい。
  ...(productionOnly
    ? [
        {
          target: PRODUCTION_DIRS.map((dir) => `./${unitSrcRel}/${dir}`),
          from: `./${unitSrcRel}/testing`,
          message:
            '本番コードからテストユーティリティ（testing/）を参照しない（ADR-0067 決定 5）。' +
            'testing/ はテスト専用の第 4 層であり、参照される側にならない。',
        },
      ]
    : []),
];

// ADR-0031 / IADR-0275: `eslint-plugin-testing-library` のうち**個別に理由があって off にする規則**。
// `export` しているのは `eslint.templates.config.js`（雛形用の入口）が同じ値を使い回すためである
// ——**雛形へ別立ての規則表を書かない**（2 本になった時点で必ず片方が古くなる。IADR-0203 追記 条件 2）。
export const TESTING_LIBRARY_RULE_OVERRIDES = {
  // ▼ ここから下の 3 つは**個別に理由があって off にしている**（一律 off ではない）。
  //   残り 19 規則（`await-async-*` / `no-await-sync-*` / `prefer-find-by` /
  //   `prefer-presence-queries` / `no-dom-import` / `no-debugging-utils` /
  //   `no-render-in-lifecycle` / `no-unnecessary-act` / `no-wait-for-*` ほか）は
  //   **error のまま**であり、導入時点の違反は 0 件である（実測 2026-08-23）。

  // `container.querySelector(...)` を禁じる規則。**本リポジトリの中核の回帰試験と衝突する。**
  // 「状態を色だけで表さない」（INDEX 決定 21 / IADR-0125 決定 1）の担保は
  // 「tone ごとに**装飾アイコン**が在り、かつ tone 間で異なること」を見ることであり、
  // 装飾アイコン（`aria-hidden="true"`）には**アクセシブルな名前が無い＝ Testing Library の
  // クエリで取れない**。規則の指示どおり `getByRole()` へ寄せると、この回帰試験は書けなくなる。
  // 代替として本番コンポーネントへ `data-testid` を足すのは、試験の都合で本番の markup を
  // 変える手であり採らない。実測 6 件（`packages/ui` の Alert / StatusBadge / Tag）。
  'testing-library/no-container': 'off',
  // `.closest()` / `.parentElement` / DOM の直接操作を禁じる規則。**行スコープの慣用と衝突する。**
  // 本リポジトリは「ある行の中だけを見る」検証を `within(link.closest('tr')!)` で書いている
  // （表の 1 行に同じ語が複数出るため、`screen` 直下のクエリでは書けない）。
  // Testing Library には「この要素を含む行」を取るクエリが無く、規則が指す代替が存在しない。
  // 実測 18 件（`packages/ui` 11 ＋ `knowledge` 4 ＋ `platform` 3）。
  'testing-library/no-node-access': 'off',
  // `render()` の戻り値の変数名を `view` / `utils` に限る規則。**aggressive reporting の誤爆である。**
  // 当プラグインは**名前が `render` で始まる関数はすべて render とみなす**ため、本リポジトリの
  // ハーネス（`renderUnitRoute` / `renderLayout` / `renderFilter` ほか）の戻り値まで対象になる。
  // それらが返すのはルータやモック関数であって render の戻り値ではない
  // （実測: `const onChange = renderFilter(...)` は **`vi.fn()` を受けている**）。
  // 加えて本リポジトリは `absent` / `unknown` のように**その試験での意味**を変数名に載せており、
  // `view` へ揃えると読み手が失う情報のほうが大きい。実測 8 件。
  'testing-library/render-result-naming-convention': 'off',
};

// ADR-0031 / IADR-0275: **既存違反の grandfather は `eslint-suppressions.json`（ESLint 9.24+ の
// 抑制ファイル。同ディレクトリ）が持つ。** 規則を弱めるのでも、ファイルを ignore するのでもなく、
// **「その時点で在った件数」だけを file × rule 単位で抑える**ので、**新しい違反はそのまま error になる**。
// 本リポジトリの他のラチェット（`scripts/knip-baseline.json` ほか）と同じ性質で、
// **使われなくなった抑制は exit 2 で落ちる**（`eslint . --prune-suppressions` で締める）。
// 中身は「承認」ではなく **grandfather** である。理由と解消の道筋は IADR-0275 §決定 4。
export default tseslint.config(
  {
    ignores: [
      '**/dist',
      '**/coverage',
      '**/playwright-report',
      '**/test-results',
      // Issue #283, IADR-0080: 可変ユニット AST の standalone 専用テスト/型検査スタブ（@foundation スタブ等）は
      // platform 合成時には使われない（合成は実 foundation を解決する）。横断 lint/coverage の対象は
      // 各ユニットの frontend/src のみとし、AST の frontend/test 配下は除外する（vitest の include とも整合）。
      'ai-stock-trading/frontend/test',
      // ADR-0031 / IADR-0121 決定 3: orval の生成物は lint 対象外（品質は生成器の責務）。
      // 乖離は `pnpm run codegen` の再実行差分（CI の codegen ステップ）で検出する。
      'platform/frontend/src/lib/api/generated',
      // ADR-0031 / IADR-0125 決定 3: lingui compile の生成物（カタログ）。同じ理由で対象外にする。
      // 乖離は `pnpm run i18n` の再実行差分と check-i18n-catalogs.js が検出する。
      'platform/frontend/src/locales',
      // ADR-0031 / IADR-0125 決定 5: Storybook の静的ビルド（生成物。gitignore 済みだが
      // ローカルにビルドが残っていると lint が数万行を走査して落ちる）。
      '**/storybook-static',
    ],
  },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    },
  },
  {
    // Node コンテキスト（設定ファイル。ワークスペースルート・各パッケージ直下とも対象）。
    files: ['**/*.config.{ts,js}', '**/playwright.config.ts'],
    languageOptions: { globals: globals.node },
  },
  // FR-14, IADR-0056 / IADR-0057, Issue #231 / #283: ユニット依存方向（フロント）の機械検査。
  // platform/frontend からの可変ユニット（@knowledge / @ai-stock-trading）参照は
  // 合成点（features/index.ts）1 箇所のみ許可する。
  //
  // ［2026-08-30 追記 / ADR-0067］**層の向きの規則（`import/no-restricted-paths`）を
  // ここへ同居させた。** #1065（IADR-0308 決定 5）は「`@foundation` の実体が `app/` 配下にあるため
  // platform へ配備できない」として knowledge にしか掛けなかったが、ADR-0067 は
  // **それは衝突ではなく層の分類の誤りである**と裁定した（`config` は原典では `app` の兄弟）。
  // 分類を直したので、**規則を 1 つも緩めずに platform へ配備できる。**
  //
  // 🔴 **`ignores` の合成点は決定 4 の実装でもある。** 合成点（`features/index.ts`）は
  // 置き場所こそ `features/` 直下だが**層としては app** であり、`features → app` の禁止に
  // 掛けてはならない（掛けると「feature を束ねる」という合成点の定義そのものが弾かれる）。
  // **除外は 1 箇所に留める** —— ゾーン側にも同じパスを書くと片方が腐る。
  {
    files: ['platform/frontend/src/**/*.{ts,tsx}'],
    ignores: ['platform/frontend/src/features/index.ts'],
    plugins: { import: importPlugin },
    // ADR-0066 決定 3 / IADR-0308: `import/no-restricted-paths` は**解決できた import しか見ない**。
    // 既定の node resolver は `.mjs/.js/.json/.node` しか試さないため、拡張子を足さないと
    // 本リポジトリの `.ts` / `.tsx` は 1 件も解決されず、規則は**静かに 0 件で通る**。
    settings: PLATFORM_RESOLVER,
    rules: {
      'import/no-restricted-paths': [
        'error',
        { basePath: CONFIG_DIR, zones: featureIsolationZones('platform/frontend/src') },
      ],
      'no-restricted-imports': [
        'error',
        {
          paths: NO_LEGACY_ROUTER_PATHS,
          patterns: [
            ...BANNED_IMPORT_PATTERNS,
            {
              group: ['@knowledge', '@knowledge/*'],
              message:
                '可変機能ユニット（@knowledge）の参照は合成点 platform/frontend/src/features/index.ts のみ許可（src/README.md 依存規則 例外2）。',
            },
            {
              group: ['@ai-stock-trading', '@ai-stock-trading/*'],
              message:
                '可変機能ユニット（@ai-stock-trading）の参照は合成点 platform/frontend/src/features/index.ts のみ許可（src/README.md 依存規則 例外2）。',
            },
          ],
        },
      ],
    },
  },
  // 可変ユニット（@knowledge）は @foundation のみ参照可。platform の合成点（@features）は参照しない。
  //
  // ［2026-08-30 / #1065］**従前ここは「`knowledge/frontend/src/` で中身を持つのは `features/` だけ
  // である」と書いていたが、その前提は既に崩れていた**（`components/` に 9 ファイル。実測 2026-08-30）。
  // #1065 で `lib/` にも実体が入った（`abac` / `scope-filter`）。よって
  // **本ブロックの適用範囲は「画面」ではなくユニット全体**であり、`apiFetch` 禁止（#555 / IADR-0146）は
  // 共有層にも掛かる —— 共有層が BFF を直接叩く形も同じ理由で望ましくないので、これは意図どおりである。
  //
  // **専用のブロックを新設しない** —— flat config は同一ルールを後勝ちで**置換**するため、
  // `features/**` を対象にした 2 本目の `no-restricted-imports` を置くと、
  // このブロックの `BANNED_IMPORT_PATTERNS` と `@features` 禁止が丸ごと無効化される
  // （本ファイル冒頭が警告している型そのもの）。**feature 境界の規則（`import/no-restricted-paths`）も
  // 同じ理由でここへ同居させる**（別規則なので置換は起きないが、ユニットの import 規約を 1 箇所に集める）。
  {
    files: ['knowledge/frontend/src/**/*.{ts,tsx}'],
    plugins: { import: importPlugin },
    settings: KNOWLEDGE_RESOLVER,
    rules: {
      'no-restricted-imports': [
        'error',
        {
          paths: [...NO_LEGACY_ROUTER_PATHS, NO_APIFETCH_IN_FEATURES, NO_BFFFETCH_IN_FEATURES],
          patterns: [
            ...BANNED_IMPORT_PATTERNS,
            {
              group: ['@features', '@features/*'],
              message:
                '可変機能ユニットは platform の合成点（@features）へ依存しない。基盤参照は @foundation のみ許可（src/README.md 依存規則 例外2）。',
            },
            {
              // ADR-0066 決定 3 / IADR-0308 決定 3 / #1065: **自ユニット内の自己参照エイリアスを塞ぐ。**
              // 下の `import/no-restricted-paths` の解決器は `@knowledge/*` を解決できないため、
              // `@knowledge/features/<B>` と書けば feature 境界の規則を**素通りする**。
              // 実測（2026-08-30）: knowledge ユニット内からの `@knowledge` 利用は 0 件
              // （唯一の利用は platform の合成点 `platform/frontend/src/features/index.ts`。別ブロックの管轄）。
              group: ['@knowledge', '@knowledge/*'],
              message:
                '自ユニット内は相対パスで参照する（ADR-0066 決定 3）。@knowledge 経由だと feature 境界の ' +
                'import/no-restricted-paths が解決できず素通りする。@knowledge は platform の合成点専用。',
            },
          ],
        },
      ],
      'import/no-restricted-paths': [
        'error',
        { basePath: CONFIG_DIR, zones: featureIsolationZones('knowledge/frontend/src') },
      ],
    },
  },
  // ADR-0067 決定 5（`testing/` は参照される側にならない）**だけ**を本番コードへ追加する。
  //
  // 🔴 **これは flat config の「同一ルールは後勝ちで置換」を意図して使っている唯一の箇所である。**
  // 上の 2 ブロックが置いた `import/no-restricted-paths` を、**同じゾーン一式 ＋ 1 本**で置き換える
  // （`featureIsolationZones(..., { productionOnly: true })`。ゾーンの本体は 1 つの関数が持つので
  // 2 本になって片方が腐ることは無い）。**`no-restricted-imports` はここで宣言しない** ——
  // 宣言すると上のブロックの禁止リストが丸ごと消える（本ファイル冒頭が警告している事故）。
  //
  // なぜ分けるのか: 決定 5 の文言は「**本番コードから**参照しない」である。テストが
  // テストユーティリティを引くのは正しい（実測: `components/notifications/NotificationBell.test.tsx`）。
  // 「テストファイルを除く」を `import/no-restricted-paths` の glob `target` で書くと、
  // minimatch が `/` 区切りを要求するため **Windows では静かに 0 件**になる。ESLint の
  // `files` / `ignores` は OS 差を吸収するので、**除外はこちらで表す**。
  {
    files: ['platform/frontend/src/**/*.{ts,tsx}'],
    ignores: [
      'platform/frontend/src/features/index.ts',
      'platform/frontend/src/**/*.{test,spec}.{ts,tsx}',
    ],
    plugins: { import: importPlugin },
    settings: PLATFORM_RESOLVER,
    rules: {
      'import/no-restricted-paths': [
        'error',
        {
          basePath: CONFIG_DIR,
          zones: featureIsolationZones('platform/frontend/src', { productionOnly: true }),
        },
      ],
    },
  },
  {
    files: ['knowledge/frontend/src/**/*.{ts,tsx}'],
    ignores: ['knowledge/frontend/src/**/*.{test,spec}.{ts,tsx}'],
    plugins: { import: importPlugin },
    settings: KNOWLEDGE_RESOLVER,
    rules: {
      'import/no-restricted-paths': [
        'error',
        {
          basePath: CONFIG_DIR,
          zones: featureIsolationZones('knowledge/frontend/src', { productionOnly: true }),
        },
      ],
    },
  },
  // Issue #283: 可変ユニット（@ai-stock-trading）も @foundation のみ参照可。platform の合成点（@features）は参照しない。
  {
    files: ['ai-stock-trading/frontend/src/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            ...BANNED_IMPORT_PATTERNS,
            {
              group: ['@features', '@features/*'],
              message:
                '可変機能ユニットは platform の合成点（@features）へ依存しない。基盤参照は @foundation のみ許可（src/README.md 依存規則 例外2）。',
            },
          ],
        },
      ],
    },
  },
  // ADR-0031 / IADR-0121 決定 8: 上の 3 ブロックが当たらないファイル（packages/ui 等）にも
  // 共通の禁止 import を効かせる。
  {
    files: ['**/*.{ts,tsx}'],
    ignores: [
      'platform/frontend/src/**/*.{ts,tsx}',
      'knowledge/frontend/src/**/*.{ts,tsx}',
      'ai-stock-trading/frontend/src/**/*.{ts,tsx}',
    ],
    rules: {
      'no-restricted-imports': ['error', { patterns: BANNED_IMPORT_PATTERNS }],
    },
  },
  // ADR-0031 / IADR-0121 決定 3 / 決定 8: BFF 境界。SPA から出る HTTP は foundation/api の 1 箇所に
  // 収束させる。features や共有 UI が直接 fetch を呼ぶと、実行時 config（環境非依存ビルド）と
  // 401 再ログイン導線を迂回してしまい、画面は動いて見えるので気付けない。
  // 例外は (1) foundation/api 自身（唯一の出口）、(2) orval 生成物（lint 対象外・mutator 経由）、
  // (3) テスト（fetch のモック定義に必要）、(4) 可変ユニット AST の standalone E2E ハーネス
  //     （platform 合成時には使われない別プロジェクトの配線。IADR-0080 / IADR-0120。既存の
  //      `ai-stock-trading/frontend/test` 除外と同じ理由で、本リポの規約を submodule へ及ぼさない）。
  {
    files: ['**/*.{ts,tsx}'],
    ignores: [
      'platform/frontend/src/lib/api/**',
      '**/*.{test,spec}.{ts,tsx}',
      '**/*.config.{ts,js}',
      'ai-stock-trading/frontend/e2e/**',
      // (5) FR-20 / ADR-0037 決定 1 / IADR-0331 決定 4・6: 自作 Obsidian プラグインの HTTP の出口。
      //     プラグインは SPA ではなく BFF も経由しない（DocumentService の同期プロトコルを Bearer
      //     同期トークンで直接呼ぶ。契約の正は docs/api/FR-20_obsidian-sync.md）ので、本規則の意図
      //     「SPA から出る HTTP を foundation/api へ収束させる」は当たらない。**出口を `transport/` の
      //     2 ファイル（Obsidian requestUrl / Node fetch）に限る**ことで同じ規律（1 箇所へ収束）を保つ——
      //     `protocol/` や `main.ts` で `fetch` を書けば本ブロックがそのまま error にする。
      'obsidian-plugin/src/transport/**',
    ],
    rules: {
      'no-restricted-globals': [
        'error',
        {
          name: 'fetch',
          message:
            'BFF へは @foundation/api（apiFetch / apiStream）または orval 生成フック経由で呼ぶ（ADR-0031）。',
        },
        {
          name: 'XMLHttpRequest',
          message: 'XMLHttpRequest は使わない。BFF 呼び出しは @foundation/api 経由（ADR-0031）。',
        },
        {
          name: 'EventSource',
          message:
            'EventSource は Authorization を付与できない。SSE は @foundation/api の apiStream を使う（IADR-0037）。',
        },
      ],
      'no-restricted-properties': [
        'error',
        {
          object: 'window',
          property: 'fetch',
          message: 'BFF へは @foundation/api 経由で呼ぶ（ADR-0031）。',
        },
        {
          object: 'globalThis',
          property: 'fetch',
          message: 'BFF へは @foundation/api 経由で呼ぶ（ADR-0031）。',
        },
      ],
    },
  },
  // ADR-0031 / IADR-0125 決定 6 / IADR-0312 / issue #1078: 13_frontend-stack §採用技術一覧 の
  // Linter 欄「Storybook / Lingui のプラグインを併用」に従う。
  //
  // 🔴 **ここを許可リストにしない。** 従前この `files` は「i18n 化済みのファイル」を 19 行で列挙し、
  // **画面を作るたびに人が伸ばす**運用だった（IADR-0125 決定 6 が SC-04〜11 の i18n 化を #452 へ
  // 繰り延べた当時は、規則を及ぼすと数百件の error が出たため合理的だった）。
  // **その繰り延べは消化済みであり、許可リストだけが残って穴になった。**
  //
  // 実測（2026-08-30 / #1078）: 列挙は **19 ファイルの i18n 済みコードを取りこぼしており**、
  // 少なくとも **4 つの独立した PR**（#1009 / #1021 / #1045 / #1065）で伸ばし忘れが develop へ入っていた。
  // ADR-0066 §理由 はこの運用を名指しで「許可リストの保守が人に戻り、伸ばし忘れが規則の穴になる」と
  // 述べている。**穴は検知するのではなく消す** ——列挙を撤去し、範囲をユニット全体で表す。
  // **画面・feature・共有ディレクトリを足しても、このファイルは触らなくてよい。**
  //
  // 範囲は `lingui.config.ts` の `catalogs[].include`（カタログ抽出範囲）と**同一**である。
  // 従前は抽出のほうが広く lint だけが狭かった——その不一致が #1078 の実体だった。**両者をずらさない。**
  {
    files: ['platform/frontend/src/**/*.{ts,tsx}', 'knowledge/frontend/src/**/*.{ts,tsx}'],
    // 生成物（orval / lingui compile）は全体の `ignores` が既に外している（本ファイル冒頭）。
    // ここで外すのはテストだけである——テストコードの文字列は UI に出ない。
    ignores: ['**/*.{test,spec}.{ts,tsx}', '**/locales/**'],
    plugins: { lingui },
    rules: {
      // 画面に出る文字列リテラルの直書きを禁じる（= 抽出されない文言を作らせない）。
      'lingui/no-unlocalized-strings': [
        'error',
        {
          // 文言ではないもの（クラス名・ロール・ルート ID・属性値）まで拾うと実質使えない。
          // 判定は「JSX のテキストと、翻訳 API へ渡す文字列」に絞る。
          //
          // **空白を含む ASCII 文字列は除外しない**（2 つ目のパターンに空白を入れない）。
          // 空白を許すと `Untranslated english text` のような**英語の文章がそのまま素通り**し、
          // 「未国際化リテラルの検出」が日本語にしか効かなくなる（実測で確認した穴）。
          // 空白を含むクラス名（`text-sm font-medium` 等）は下の ignoreNames（属性名）が拾う。
          //
          // **残る限界**: 空白を含まない ASCII トークン（`Docs` 等）は素通りする。識別子・列挙値・
          // ルート ID・クラス名の断片と区別できないためで、これは意図的に残す
          // （厳しくすると誤検出が実用の域を超え、規則ごと無効化される方が高くつく）。
          //
          // ［2026-08-30 / #1078］**HTML のタグと実体参照だけで出来た文字列は markup であって文言ではない。**
          // 適用範囲をユニット全体へ広げた際、`escapeHtml` の置換先（`&amp;` `&lt;` …）と
          // ECharts の tooltip formatter が組む断片（`<br/>` `</b><br/>`）が拾われた。
          // **語を 1 つも含まない文字列**しか当たらないため、未国際化の文言を隠すことはない
          // （`<b>Save</b>` のように語を含めば当たらず、従来どおり error になる）。
          ignore: ['^[a-z0-9-]+$', '^[A-Za-z0-9_./:#$?&=@%+-]*$', '^(?:<[^>]*>|&[a-z]+;)+$'],
          ignoreNames: [
            {
              regex: { pattern: '^(className|id|role|to|from|href|src|type|name|key|scope|path)$' },
            },
          ],
          // 開発者向けの例外メッセージは UI ではない（利用者の目に触れない）。
          // 翻訳すると、障害時のログと検索性を落とす。
          ignoreFunctions: ['Error', 'TypeError', 'console.*'],
        },
      ],
      // マクロの誤用（式の埋め込み・翻訳単位の分割）はカタログを壊す。
      'lingui/no-expression-in-message': 'error',
      'lingui/no-single-variables-to-translate': 'error',
      'lingui/t-call-in-function': 'error',
    },
  },
  // ADR-0031 / IADR-0275: 13_frontend-stack §採用技術一覧 の Linter 欄
  // 「**TanStack** / **Testing Library** / Storybook / Lingui のプラグインを併用」のうち、
  // 落ちていた TanStack（Query / Router）と Testing Library を足す（issue #493）。
  //
  // **`ai-stock-trading/**` は適用範囲から外す。** 別プロジェクトの submodule であり本リポジトリからは
  // 是正できない（IADR-0120）。既存の `NO_LEGACY_ROUTER_PATHS` が同じ理由で AST を外しているのと
  // 同じ線引きである（**規則を弱めるのではなく、他リポジトリへ本リポの規約を及ぼさない**）。
  // 実測（2026-08-23）: AST には `@tanstack/react-query` / `@tanstack/react-router` の利用が 0 件、
  // Testing Library の利用が 15 ファイルある。
  ...tanstackQuery.configs['flat/recommended'].map((config) => ({
    ...config,
    files: ['**/*.{ts,tsx}'],
    ignores: ['ai-stock-trading/**'],
  })),
  ...tanstackRouter.configs['flat/recommended'].map((config) => ({
    ...config,
    name: 'tanstack/router/flat/recommended',
    files: ['**/*.{ts,tsx}'],
    ignores: ['ai-stock-trading/**'],
  })),
  // **Testing Library の規則は Vitest の単体テストにだけ効かせる。**
  // Playwright の E2E（`**/e2e/**`）を含めてはならない —— 当プラグインは "aggressive reporting" で
  // **名前の形だけ**から Testing Library の利用を推測するため、Playwright の `page.getByRole(...)` を
  // Testing Library のクエリと誤認し、`prefer-screen-queries` が全 E2E で error になる
  // （実測 2026-08-23: `platform/frontend/e2e/*.smoke.spec.ts` で 13 件。**いずれも Testing Library を
  // 一切 import していないファイルである**）。これは規則の無効化ではなく**射程の確定**である。
  {
    ...testingLibrary.configs['flat/react'],
    files: ['**/*.{test,spec}.{ts,tsx}'],
    ignores: ['ai-stock-trading/**', '**/e2e/**'],
    rules: {
      ...testingLibrary.configs['flat/react'].rules,
      ...TESTING_LIBRARY_RULE_OVERRIDES,
    },
  },
  // Storybook の stories に対する規約（既定の recommended）。
  ...storybook.configs['flat/recommended'],
);
