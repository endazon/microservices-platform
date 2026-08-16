import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';
import {
  BANNED_IMPORT_PATTERNS,
  NO_APIFETCH_IN_FEATURES,
  NO_BFFFETCH_IN_FEATURES,
  NO_LEGACY_ROUTER_PATHS,
} from './eslint.config.js';

// FR-14 / IADR-0056 / IADR-0060: 追加可変機能ユニットの**雛形**（`templates/*/frontend`）用の lint 設定。
//
// **なぜ `eslint.config.js` に相乗りできないのか。** ESLint の flat config は「設定ファイルのある
// ディレクトリ」を実行の基準にし、**その外のファイルを検査しない**（実測: `src/` から
// `../templates/...` を渡すと "all of the files matching the glob pattern are ignored /
// … located outside of the base path" で終わる）。したがって
//   - `src/` から実行する既存の `eslint .` は、`templates/` へ到達できない
//   - リポジトリルートから実行すると到達できるが、そのとき基準はルートになり、
//     `eslint.config.js` のユニット別 `files`（`platform/frontend/src/**` 等）が**一致しなくなる**
// という二者択一になる。1 つの設定で両方を正しく賄えないため、**雛形専用の入口を分ける**。
//
// **禁止リストは複製せず `eslint.config.js` から import する**（単一情報源）。ここへ書き写すと、
// 規約を更新したときに雛形側だけ古い規則で緑になる——それは本設定が防ごうとしている事故そのものである。
//
// 実行はリポジトリルートを cwd として行う（`src/package.json` の `lint:templates`）。
// したがって下の `files` はルートからの相対で書く。
export default tseslint.config(
  {
    ignores: ['**/dist', '**/node_modules'],
  },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ['templates/*/frontend/src/**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      ...reactHooks.configs.recommended.rules,
      // 雛形は可変ユニットの出発点なので、**`knowledge/frontend/src/**` へ課しているのと同じ規則一式**を
      // 課す（ADR-0031 / IADR-0121 決定 8 / IADR-0124 / IADR-0146）。雛形の `src/` の中身は
      // `features/` だけであり、knowledge と同じく「適用範囲がそのまま画面の範囲」になる。
      //
      // **`paths` と `patterns` は 1 つの no-restricted-imports にまとめて渡す。**
      // 同じルールを 2 回書くと後勝ちで一方が消える（flat config の上書き規則。
      // `eslint.config.js` 冒頭が警告している型そのもの）。
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
);
