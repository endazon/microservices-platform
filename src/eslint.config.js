import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// ADR-0031 / IADR-0121 決定 8: 新スタックの禁止事項を機械強制する。専用の検査スクリプトを作らないのは、
// 対象が import と識別子の静的検査＝ESLint の守備範囲そのものであり、検査器を増やすほど
// 「走らせ忘れ」と二重メンテが増えるためである。
// 各ユニットのブロックが no-restricted-imports を上書きしてしまうため（flat config は同一ルールを
// 後勝ちで置換する）、共通パターンは定数にして各ブロックへ必ず展開する。
const BANNED_IMPORT_PATTERNS = [
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
      '手書きの HTTP クライアントは禁止（ADR-0031）。BFF 呼び出しは orval 生成フック、'
      + 'または @foundation/api の apiFetch / apiStream を使う。',
  },
  {
    // IADR-0121 決定 4: 共有 UI の公開面は @platform/ui のエントリのみ。
    group: ['@platform/ui/src', '@platform/ui/src/*'],
    message:
      '@platform/ui の内部実装を直接参照しない。公開面は "@platform/ui" と "@platform/ui/styles.css" のみ。',
  },
];

// ADR-0031 / IADR-0124: ルーティングは TanStack Router に一本化する（移行第 2 段 / #490）。
// 「各系統は 1 度だけ切り替え、2 つのルータが同時に存在する状態を作らない」（IADR-0121 決定 1）を
// 機械で守る。本リポジトリが所有する platform / knowledge にのみ適用する——`ai-stock-trading` は
// 別プロジェクトの submodule（IADR-0120）であり、本リポの規約を及ぼさない（旧契約ブリッジで動く）。
// `patterns` ではなく `paths`（完全一致）で指定する——`patterns` は matchBase で照合するため、
// `react-router` は `@tanstack/react-router` にも当たってしまう（実測）。
const NO_LEGACY_ROUTER_PATHS = ['react-router', 'react-router-dom'].map((name) => ({
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
  {
    files: ['knowledge/frontend/src/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          paths: NO_LEGACY_ROUTER_PATHS,
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
        { object: 'window', property: 'fetch', message: 'BFF へは @foundation/api 経由で呼ぶ（ADR-0031）。' },
        { object: 'globalThis', property: 'fetch', message: 'BFF へは @foundation/api 経由で呼ぶ（ADR-0031）。' },
      ],
    },
  },
);
