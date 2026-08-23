import { test, expect } from '@playwright/test';

// SC-02 (#502): スクリーンレベル・スモーク（バックエンド不要）。
// /search は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 検索・一覧・SC-03 への遷移は Vitest（単体＋導線テスト searchFlow.test.tsx）で検証する
// ——セッションは BFF（Keycloak）との往復で成立し、プレビューにはどちらも無いため、認証済みの
// 導線は Playwright で実走できない（docs/tests/SC-01_search-chat.md §E2E の限界）。
test('unauthenticated visit to /search redirects to /login', async ({ page }) => {
  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
