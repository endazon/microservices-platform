import { defineConfig, devices } from '@playwright/test';

// NFR-30 / SC-13〜16 (#2191 / IADR-0619): **Keycloak のテーマの画面を axe で走査する**ための config。
//
// L1（`playwright.config.ts`）は基盤を 1 つも起動しない層で、Keycloak のテーマの画面は描けない（IADR-0614 決定 3）。
// 本 config は **CI のジョブの中で起こした使い捨ての Keycloak**（`.github/workflows/keycloak-theme-a11y.yml`。
// 配布物と同じ版のイメージに、リポジトリのテーマと realm を載せる）へ向けて走る。稼働の Keycloak へは向けない
// （走査用の利用者を作って消すため）。
//
// - 接続先は `E2E_KC_URL`、管理者は `E2E_KC_ADMIN_USER` / `E2E_KC_ADMIN_PASSWORD`（`e2e/keycloak/support.ts`）。
//   **未設定なら読み込みで落とす** —— 0 件の緑にしない（IADR-0569 決定 4 と同じ思想）。
// - spec の接尾辞は `.kc.ts` である。L1 の config は既定の testMatch（`*.spec.ts` / `*.test.ts`）で `e2e/` 全体を拾うため、
//   `.spec.ts` にすると基盤なしの L1 で走って落ちる。
// - 明暗は project で与える（Keycloak のテーマは `prefers-color-scheme` だけで明暗が決まる。IADR-0532 決定 3）。
//   **project 名がそのまま台帳のテーマの値**になるわけではない —— 台帳のキーのテーマは、走査時にページの
//   `prefers-color-scheme` から読む（`support.ts`）。

const KC_URL = process.env.E2E_KC_URL;
if (!KC_URL) {
  throw new Error('E2E_KC_URL が未設定（使い捨ての Keycloak の URL。例 http://localhost:8080）');
}

export default defineConfig({
  testDir: './e2e/keycloak',
  testMatch: '**/*.kc.ts',
  // 1 本の test がログイン → OTP の登録 → パスワードの更新 → アカウントコンソールまで歩くため、L1 より長い。
  timeout: 120_000,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  // 走査用の利用者は test ごとに作るが、realm の総当たり検知（failureFactor）を同じ利用者で踏まないよう直列にする。
  workers: 1,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: KC_URL,
    trace: 'on-first-retry',
    // realm の既定ロケールは ja（L1 と同じく ja に固定する）。
    locale: 'ja-JP',
  },
  projects: [
    { name: 'keycloak-light', use: { ...devices['Desktop Chrome'], colorScheme: 'light' } },
    { name: 'keycloak-dark', use: { ...devices['Desktop Chrome'], colorScheme: 'dark' } },
  ],
});
