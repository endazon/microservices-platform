import { defineConfig, devices } from '@playwright/test';

// Issue #126: スクリーンレベルの e2e スモーク。ビルド済みプレビューに対して実行し、バックエンド
// （Keycloak/BFF）不要のログイン画面到達までを検証する。SC-01..11 は各 feature で e2e を拡張する。

// Issue #554: プレビューの接続先はここだけで決める。以前は同じ値が baseURL /
// webServer.command の --port / webServer.url の 3 箇所に複写されており、さらに
// e2e 側にも直書きされていた（値の複写はいずれ必ずずれる）。
const PREVIEW_PORT = 4173;
const PREVIEW_URL = `http://localhost:${PREVIEW_PORT}`;

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: PREVIEW_URL,
    trace: 'on-first-retry',
    // ADR-0031 / IADR-0125 決定 7: 表示言語はブラウザの言語設定から決まる（切替 UI は持たない）。
    // Playwright の既定は en-US であり、固定しないと **CI の既定ロケール次第でアサーションが割れる**
    // （実測: 固定前は英語で描画され、日本語の見出しを待つスモークが落ちた）。
    // 単体テスト側を setup.ts で ja に固定しているのと同じ理由・同じ値である。
    locale: 'ja-JP',
  },
  webServer: {
    command: `pnpm run preview -- --port ${PREVIEW_PORT} --strictPort`,
    url: PREVIEW_URL,
    // Issue #554: **ローカルでも常に自前で起動する**（既定の `!process.env.CI` を採らない）。
    // 理由: `--strictPort` は「このポートを占有できなければ失敗する」という宣言であり、
    // 再利用を許すと**同じポートに居る無関係なプロセスを本アプリだと思って接続する**。
    // #519 の作業中に実際にこの経路を踏んでおり、E2E が別サーバーを叩いていた。
    // 常に自前起動なら、占有時は `--strictPort` が**起動失敗として大きく落ちる**——
    // 本 issue が正そうとしている「空振りが緑になる」の逆で、原因が即座に判る。
    // 代償はローカルでの起動待ち（実測 1 秒程度）であり、取り違えの危険に見合わない。
    reuseExistingServer: false,
    timeout: 60_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
