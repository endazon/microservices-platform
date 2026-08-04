import { test, expect } from '@playwright/test';

// SC-03 (#502): スクリーンレベル・スモーク（バックエンド不要）。
// /docs/:id は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// **未知のパスとして 404 になるのではなく、認証ガードが先に効く**ことを見る
// （ルートが登録されていないと NotFound が出て /login へ行かないため、この 1 本で
//   「ルートが実在すること」も同時に固定できる）。
test('unauthenticated visit to /docs/:id redirects to /login', async ({ page }) => {
  await page.goto('/docs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
