import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// FR-14, IADR-0056: 単体テスト＋カバレッジはワークスペースルートで全ユニット
// （platform / knowledge）を横断計測する。ビルド・dev サーバは platform/vite.config.ts。
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./platform/src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./platform/src/features', import.meta.url)),
      '@knowledge': fileURLToPath(new URL('./knowledge/src', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./platform/src/test/setup.ts'],
    include: [
      'platform/src/**/*.{test,spec}.{ts,tsx}',
      'knowledge/src/**/*.{test,spec}.{ts,tsx}',
    ],
    css: false,
    // IADR-0033/0034: カバレッジはしきい値ゲート（回帰防止のラチェット）。CI(frontend-tests.yml)
    // でレポート生成＋ゲートに用いる。テストを増やしたらしきい値を引き上げる。
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // 計測対象は各ユニット src 配下の実装のみ。テスト・型定義・エントリ/自動生成は除外する。
      include: ['platform/src/**/*.{ts,tsx}', 'knowledge/src/**/*.{ts,tsx}'],
      exclude: [
        '**/*.{test,spec}.{ts,tsx}',
        'platform/src/test/**',
        '**/*.d.ts',
        'platform/src/main.tsx',
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
