import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// Issue #126 / SC-01..11 SPA 基盤: BFF を境界にした疎結合構成。
// - dev proxy: /bff/* を BFF（既定 http://localhost:5000）へ転送する。接続先は VITE_BFF_TARGET で上書き可能。
// - 実行時 config（public/config.js）で本番の接続先を注入するため、ビルドは環境非依存に保つ。
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@foundation': fileURLToPath(new URL('./src/foundation', import.meta.url)),
      '@features': fileURLToPath(new URL('./src/features', import.meta.url)),
    },
  },
  server: {
    port: 3100,
    proxy: {
      '/bff': {
        target: process.env.VITE_BFF_TARGET ?? 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    css: false,
    // IADR-0033: フロントエンド単体テストのカバレッジ計測。CI(frontend-tests.yml)で
    // レポート生成＋しきい値ゲート（回帰防止のラチェット）に用いる。
    coverage: {
      provider: 'v8',
      reporter: ['text', 'text-summary', 'html', 'lcov'],
      reportsDirectory: './coverage',
      // 計測対象は src 配下の実装のみ。テスト・型定義・エントリ/自動生成は除外する。
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.{test,spec}.{ts,tsx}',
        'src/test/**',
        'src/**/*.d.ts',
        'src/main.tsx',
        'src/vite-env.d.ts',
      ],
      // 回帰防止のラチェット。SPA 基盤(#126)時点の実測値を下限として据え、SC-01..11 の
      // 各画面がテストを増やすたびに引き上げる。現状: 未テストの UI コンポーネントが多く
      // 全体値は低いが、この床を割る変更（テスト削除・無検証コード追加）を CI で止める。
      thresholds: {
        lines: 25,
        statements: 25,
        functions: 40,
        branches: 60,
      },
    },
  },
});
