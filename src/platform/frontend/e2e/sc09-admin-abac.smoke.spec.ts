import { test, expect } from '@playwright/test';

// SC-09 (#504): スクリーンレベル・スモーク（バックエンド不要）。
// /admin/abac は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// **未知のパスとして 404 になるのではなく、認証ガードが先に効く**ことを見る
// （ルートが登録されていないと NotFound が出て /login へ行かないため、この 1 本で
//   「ルートが実在すること」も同時に固定できる）。
// 属性辞書・ポリシー定義・検証結果・権限別の出し分けは Vitest（単体）で検証する——セッションは
// BFF（Keycloak）との往復で成立し、プレビューにはどちらも無いため、認証済みの導線は Playwright で実走できない。
test('unauthenticated visit to /admin/abac redirects to /login', async ({ page }) => {
  await page.goto('/admin/abac');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
