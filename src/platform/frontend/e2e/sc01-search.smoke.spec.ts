import { test, expect } from '@playwright/test';

// SC-01 (#127): スクリーンレベル・スモーク（バックエンド不要）。
// /ask は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 検索・SSE ストリーミング・出典・フィードバックの挙動は Vitest（単体）で検証する。
test('unauthenticated visit to /ask redirects to /login', async ({ page }) => {
  await page.goto('/ask');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
