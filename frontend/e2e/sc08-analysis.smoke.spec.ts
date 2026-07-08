import { test, expect } from '@playwright/test';

// SC-08 (#134): スクリーンレベル・スモーク（バックエンド不要）。
// /analysis は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 認証済み時のフォーム送信・結果描画・空縮退・異常系は Vitest（単体）で検証する。
test('unauthenticated visit to /analysis redirects to /login', async ({ page }) => {
  await page.goto('/analysis');

  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
