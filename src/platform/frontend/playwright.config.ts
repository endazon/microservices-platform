import { defineConfig, devices } from '@playwright/test';

// Issue #126: スクリーンレベルの e2e スモーク。ビルド済みプレビューに対して実行し、バックエンド
// （Keycloak/BFF）不要のログイン画面到達までを検証する。SC-01..11 は各 feature で e2e を拡張する。
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: 'http://localhost:4173',
    trace: 'on-first-retry',
    // ADR-0031 / IADR-0125 決定 7: 表示言語はブラウザの言語設定から決まる（切替 UI は持たない）。
    // Playwright の既定は en-US であり、固定しないと **CI の既定ロケール次第でアサーションが割れる**
    // （実測: 固定前は英語で描画され、日本語の見出しを待つスモークが落ちた）。
    // 単体テスト側を setup.ts で ja に固定しているのと同じ理由・同じ値である。
    locale: 'ja-JP',
  },
  webServer: {
    command: 'pnpm run preview -- --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
