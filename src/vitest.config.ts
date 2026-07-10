import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// FR-14, IADR-0056: 単体テスト＋カバレッジはワークスペースルート（src/）で全ユニット
// （platform / knowledge の各 frontend）を横断計測する。ビルド・dev サーバは
// platform/frontend/vite.config.ts。
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./platform/frontend/src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./platform/frontend/src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('./knowledge/frontend/src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./platform/frontend/src/test/setup.ts'],
    include: [
      'platform/frontend/src/**/*.{test,spec}.{ts,tsx}',
      'knowledge/frontend/src/**/*.{test,spec}.{ts,tsx}',
    ],
    css: false,
    // IADR-0033/0034: カバレッジはしきい値ゲート（回帰防止のラチェット）。CI(frontend-tests.yml)
    // でレポート生成＋ゲートに用いる。テストを増やしたらしきい値を引き上げる。
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // 計測対象は各ユニット frontend/src 配下の実装のみ。テスト・型定義・エントリ/自動生成は除外する。
      include: ['platform/frontend/src/**/*.{ts,tsx}', 'knowledge/frontend/src/**/*.{ts,tsx}'],
      exclude: [
        '**/*.{test,spec}.{ts,tsx}',
        'platform/frontend/src/test/**',
        '**/*.d.ts',
        'platform/frontend/src/main.tsx',
        '**/vite-env.d.ts',
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
