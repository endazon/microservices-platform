import { test, expect } from '@playwright/test';

// SC-04 (#130): スクリーンレベル・スモーク（バックエンド不要）。
// /wiki は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// Wiki.js への SSO 遷移・ABAC 到達確認は Wiki.js/ゲートウェイ側（#118 実測済）で担保する。
test('unauthenticated visit to /wiki redirects to /login', async ({ page }) => {
  await page.goto('/wiki');

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
