import { defineConfig } from 'orval';

// ADR-0031 / IADR-0121 決定 3: BFF の OpenAPI から型・TanStack Query フック・MSW モックを生成する。
// **手書きの API クライアントは禁止**（13_frontend-stack §基本方針）。生成物はコミットし、CI で
// 「再生成しても差分が出ない」ことを検査する（pnpm run codegen && git diff --exit-code）。
export default defineConfig({
  bff: {
    input: {
      target: '../docs/api/openapi.yaml',
      // /bff/* 以外を落とす前処理。理由は orval-bff-only.cjs のコメントを参照。
      override: { transformer: './orval-bff-only.cjs' },
    },
    output: {
      target: './platform/frontend/src/foundation/api/generated/bff.ts',
      // tags-split: タグごとにファイルを分け、画面（feature）から必要なものだけを import できるようにする。
      mode: 'tags-split',
      client: 'react-query',
      httpClient: 'fetch',
      // MSW モックを生成する。コンポーネントテストを外部非依存にするため（#446 退行防止）。
      mock: true,
      prettier: true,
      clean: true,
      override: {
        // HTTP の出口を foundation/api へ集約する（実行時 config と 401 導線を効かせる）。
        mutator: {
          path: './platform/frontend/src/foundation/api/orvalMutator.ts',
          name: 'bffFetch',
        },
      },
    },
  },
});
