import { test, expect } from '@playwright/test';

// SC-03 (#502): スクリーンレベル・スモーク（バックエンド不要）。
// /docs/:id は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 🔴 **本テストが固定するのは「認証ガードが先に効くこと」だけである。ルートの実在は固定できない。**
// 未知のパスの受け皿（catchAllRoute）は `RequireAuth` 配下の shellRoute の子であるため、
// **ルートが存在しなくても未認証なら /login へ行く**（#918 が改名の変異で実測。#1013 で全数を是正した）。
// ルートの実在は router.test.ts（計画のルート表とナビ項目の解決）が Vitest 側で固定している。
test('unauthenticated visit to /docs/:id redirects to /login', async ({ page }) => {
  await page.goto('/docs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
