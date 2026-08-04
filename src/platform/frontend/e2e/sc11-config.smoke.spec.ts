import { test, expect } from '@playwright/test';

// SC-11 (#137): スクリーンレベル・スモーク（バックエンド不要）。
// /admin/config-viewer は認証必須（RequireAuth）配下かつ ConfigViewer 限定（RequireRole）。未認証で開くと
// 認証ガードにより /login へ誘導される。ロール別の存在秘匿（権限外→NotFound）は Vitest（#140）で検証する。
test('unauthenticated visit to /admin/config-viewer redirects to /login', async ({ page }) => {
  await page.goto('/admin/config-viewer');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
