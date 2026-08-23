import { test, expect } from '@playwright/test';

// SC-18 (#917): スクリーンレベル・スモーク（バックエンド不要）。
// /graph は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 認証済み時の描画・空縮退・フィルタ・サイドパネルは Vitest（単体）で検証する。
test('unauthenticated visit to /graph redirects to /login', async ({ page }) => {
  await page.goto('/graph');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
