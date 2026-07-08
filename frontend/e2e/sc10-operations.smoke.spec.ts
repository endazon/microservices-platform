import { test, expect } from '@playwright/test';

// SC-10 (#136): スクリーンレベル・スモーク（バックエンド不要）。
// SC-10 のルート /ops は認証必須（RequireAuth）配下にある。未認証で開くと認証ガードにより
// /login へ誘導される。OIDC はサインインボタン押下まで発火しないため Keycloak/BFF 不要で検証できる。
// 認証済み時のロール別描画・存在秘匿・データ表示は Vitest（単体）で検証する。
test('unauthenticated visit to /ops redirects to /login', async ({ page }) => {
  await page.goto('/ops');

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
