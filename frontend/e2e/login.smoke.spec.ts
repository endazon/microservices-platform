import { test, expect } from '@playwright/test';

// Issue #126: スケルトンのスクリーンレベル・スモーク。
// 未認証で "/" を開くと認証ガードにより /login へ誘導され、Keycloak サインインボタンが表示される。
// OIDC はボタン押下まで発火しないため、バックエンド（Keycloak/BFF）不要で検証できる。
test('unauthenticated visit redirects to /login with a Keycloak sign-in button', async ({ page }) => {
  await page.goto('/');

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Knowledge Platform' })).toBeVisible();
});
