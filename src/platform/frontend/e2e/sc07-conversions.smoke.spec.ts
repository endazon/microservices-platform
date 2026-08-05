import { test, expect } from '@playwright/test';

// SC-07 (#503): スクリーンレベル・スモーク（バックエンド不要）。
// /admin/conversions は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// **未知のパスとして 404 になるのではなく、認証ガードが先に効く**ことを見る
// （ルートが登録されていないと NotFound が出て /login へ行かないため、この 1 本で
//   「ルートが実在すること」も同時に固定できる）。
// 一覧・権限別の出し分け・エラー状態は Vitest（単体）で検証する——トークンは
// InMemoryWebStorage に保持され外部から注入できないため、認証済みの導線は Playwright で実走できない。
test('unauthenticated visit to /admin/conversions redirects to /login', async ({ page }) => {
  await page.goto('/admin/conversions');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
