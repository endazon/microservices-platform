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
  await expect(page.getByRole('heading', { name: '汎用プラットフォーム' })).toBeVisible();
});
