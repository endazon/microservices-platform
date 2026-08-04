import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// FR-14, IADR-0056: 単体テスト＋カバレッジはワークスペースルート（src/）で全ユニット
// （platform / knowledge の各 frontend）を横断計測する。ビルド・dev サーバは
// platform/frontend/vite.config.ts。
export default defineConfig({
  plugins: [react()],
  resolve: {
    // IADR-0121 決定 2（pnpm workspace）: pnpm は node_modules を isolated に置くため、ユニットごとに
    // 別々の React 実体が解決され得る（同一プロセスで 2 つの React が動くと「Invalid hook call」になる）。
    // 横断テストは 1 プロセスで全ユニットのコンポーネントを描画するので、React を明示的に重複排除する。
    dedupe: ['react', 'react-dom'],
    alias: {
      '@foundation': fileURLToPath(new URL('./platform/frontend/src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./platform/frontend/src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('./knowledge/frontend/src', import.meta.url)),
      // Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの feature テストも横断収集する。
      '@ai-stock-trading': fileURLToPath(new URL('./ai-stock-trading/frontend/src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./platform/frontend/src/test/setup.ts'],
    include: [
      'platform/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'knowledge/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'ai-stock-trading/frontend/src/**/*.{test,spec}.{ts,tsx}',
      // IADR-0121 決定 4: 共有 UI パッケージ（@platform/ui）も横断計測の対象にする。
      'packages/*/src/**/*.{test,spec}.{ts,tsx}',
    ],
    css: false,
    // IADR-0033/0034: カバレッジはしきい値ゲート（回帰防止のラチェット）。CI(frontend-tests.yml)
    // でレポート生成＋ゲートに用いる。テストを増やしたらしきい値を引き上げる。
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // 計測対象は各ユニット frontend/src 配下の実装のみ。テスト・型定義・エントリ/自動生成は除外する。
      include: [
        'platform/frontend/src/**/*.{ts,tsx}',
        'knowledge/frontend/src/**/*.{ts,tsx}',
        'ai-stock-trading/frontend/src/**/*.{ts,tsx}',
        'packages/*/src/**/*.{ts,tsx}',
      ],
      exclude: [
        '**/*.{test,spec}.{ts,tsx}',
        'platform/frontend/src/test/**',
        '**/*.d.ts',
        'platform/frontend/src/main.tsx',
        '**/vite-env.d.ts',
        // IADR-0121 決定 3: orval の生成物は計測対象外（自動生成物の品質は生成器の責務であり、
        // 母数へ入れると床が「生成量」で動いて意味を失う）。
        'platform/frontend/src/foundation/api/generated/**',
      ],
      // 回帰防止のラチェット。実測（lines/statements≈83%, branches≈80%, functions≈77-80%）
      // に合わせて床を引き上げ、床を割る変更を CI で止める（レビュー #168 指摘対応）。
      thresholds: {
        lines: 78,
        statements: 78,
        functions: 68,
        branches: 74,
      },
    },
  },
});
