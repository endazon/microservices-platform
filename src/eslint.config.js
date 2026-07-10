import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['**/dist', '**/coverage', '**/playwright-report', '**/test-results'] },
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
  // FR-14, IADR-0056 / IADR-0057, Issue #231: ユニット依存方向（フロント）の機械検査。
  // platform/frontend からの @knowledge 参照は合成点（features/index.ts）1 箇所のみ許可する。
  {
    files: ['platform/frontend/src/**/*.{ts,tsx}'],
    ignores: ['platform/frontend/src/features/index.ts'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@knowledge', '@knowledge/*'],
              message:
                '可変機能ユニット（@knowledge）の参照は合成点 platform/frontend/src/features/index.ts のみ許可（src/README.md 依存規則 例外2）。',
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
          patterns: [
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
);
