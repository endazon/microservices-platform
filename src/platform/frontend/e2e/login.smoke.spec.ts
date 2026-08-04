import { test, expect } from '@playwright/test';

// Issue #126: スケルトンのスクリーンレベル・スモーク。
// 未認証で "/" を開くと認証ガードにより /login へ誘導され、Keycloak サインインボタンが表示される。
// OIDC はボタン押下まで発火しないため、バックエンド（Keycloak/BFF）不要で検証できる。
test('unauthenticated visit redirects to /login with a Keycloak sign-in button', async ({ page }) => {
  await page.goto('/');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
  // 05_screens §共通シェル: ブランド表示名は「汎用プラットフォーム」。
  // これは **en カタログでも訳していない**ため、ロケールに依存しない（IADR-0125 決定 7 の注記）。
  await expect(page.getByRole('heading', { name: '汎用プラットフォーム' })).toBeVisible();
  // ADR-0031 / IADR-0125 決定 7: 表示言語はブラウザの言語設定で決まる。
  // playwright.config.ts の `locale: 'ja-JP'` が効いていることを、**翻訳される文言**で確かめる
  // （固定を外すと Playwright 既定の en-US となり英語で描画されて落ちる＝固定が load-bearing である）。
  await expect(page.getByText('社内ナレッジ検索・AI 回答プラットフォーム')).toBeVisible();
});
