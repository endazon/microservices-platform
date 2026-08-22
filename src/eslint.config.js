import js from '@eslint/js';
import globals from 'globals';
import lingui from 'eslint-plugin-lingui';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import storybook from 'eslint-plugin-storybook';
import tseslint from 'typescript-eslint';

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
      'platform/frontend/src/foundation/api/generated',
      // ADR-0031 / IADR-0125 決定 3: lingui compile の生成物（カタログ）。同じ理由で対象外にする。
      // 乖離は `pnpm run i18n` の再実行差分と check-i18n-catalogs.js が検出する。
      '**/foundation/i18n/locales',
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
  {
    files: ['platform/frontend/src/**/*.{ts,tsx}'],
    ignores: ['platform/frontend/src/features/index.ts'],
    rules: {
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
  // **`knowledge/frontend/src/` で中身を持つのは `features/` だけである**（実測 2026-08-23。#785 で
  // 計画 13_frontend-stack §ディレクトリ構成 へ適合させ、`app/ assets/ components/ hooks/ lib/
  // locales/ stores/ testing/ types/ utils/` を置いたが、いずれも `.gitkeep` だけの枠である）。
  // したがって本ブロックの適用範囲がそのまま「画面」の範囲であり、#555 の `apiFetch` 禁止を
  // ここへ足せば足りる。**枠へ実体が入ったらこの前提を引き直すこと。**
  // **専用のブロックを新設しない** —— flat config は同一ルールを後勝ちで**置換**するため、
  // `features/**` を対象にした 2 本目の `no-restricted-imports` を置くと、
  // このブロックの `BANNED_IMPORT_PATTERNS` と `@features` 禁止が丸ごと無効化される
  // （本ファイル冒頭が警告している型そのもの）。
  {
    files: ['knowledge/frontend/src/**/*.{ts,tsx}'],
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
          ],
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
      'platform/frontend/src/foundation/api/**',
      '**/*.{test,spec}.{ts,tsx}',
      '**/*.config.{ts,js}',
      'ai-stock-trading/frontend/e2e/**',
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
  // ADR-0031 / IADR-0125 決定 6: 13_frontend-stack §採用技術一覧 の Linter 欄
  // 「Storybook / Lingui のプラグインを併用」に従う。
  //
  // **Lingui 規則の適用先は i18n 化済みのファイルに限る。** 残る画面（SC-04〜11）は
  // #452 の残り分割が作り直すため文言を触っておらず（IADR-0125 決定 6）、いま規則を及ぼすと
  // 「その issue では直さないと決めた箇所」の error が数百件出る。
  // **#502 で SC-01〜03 を再実装したため、その 3 feature を適用範囲へ加えた**
  // （#496 §親への申し送り「`eslint-plugin-lingui` の適用範囲の拡大」の引き受け）。
  // 画面を作り直すたびにこの files を伸ばす——「i18n 化したのに検査されない」状態を残さないためである。
  {
    files: [
      'platform/frontend/src/foundation/i18n/**/*.{ts,tsx}',
      'platform/frontend/src/foundation/ui/**/*.{ts,tsx}',
      // #788（移行第 4 段）: 右レール AI チャットパネル。共通シェルに載る文言なので、
      // foundation/ui と同じ規則の下に置く。
      'platform/frontend/src/foundation/ai-chat/**/*.{ts,tsx}',
      'platform/frontend/src/foundation/auth/**/*.{ts,tsx}',
      'platform/frontend/src/foundation/routing/nav.ts',
      'knowledge/frontend/src/features/sc01-search/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc02-results/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc03-document/**/*.{ts,tsx}',
      // #503 で SC-05〜08 を再実装したため適用範囲へ加えた。`abac/` は SC-05 / SC-06 が共有する
      // 語彙（機密区分の値集合）であり、同じ規則の下に置く。
      'knowledge/frontend/src/features/abac/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc05-documents/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc06-datasources/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc07-conversions/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc08-analysis/**/*.{ts,tsx}',
      // #504 で SC-09〜11 を再実装したため適用範囲へ加えた（画面を作り直すたびに files を伸ばす運用）。
      'knowledge/frontend/src/features/sc09-admin-abac/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc10-operations/**/*.{ts,tsx}',
      'knowledge/frontend/src/features/sc11-config/**/*.{ts,tsx}',
    ],
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
          ignore: ['^[a-z0-9-]+$', '^[A-Za-z0-9_./:#$?&=@%+-]*$'],
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
  // Storybook の stories に対する規約（既定の recommended）。
  ...storybook.configs['flat/recommended'],
);
