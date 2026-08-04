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
  },
  webServer: {
    command: 'pnpm run preview -- --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
