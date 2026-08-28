import { test, expect } from '@playwright/test';

// SC-07 (#503): スクリーンレベル・スモーク（バックエンド不要）。
// /admin/conversions は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
// 🔴 **本テストが固定するのは「認証ガードが先に効くこと」だけである。ルートの実在は固定できない。**
// 未知のパスの受け皿（catchAllRoute）は `RequireAuth` 配下の shellRoute の子であるため、
// **ルートが存在しなくても未認証なら /login へ行く**（#918 が改名の変異で実測。#1013 で全数を是正した）。
// ルートの実在は router.test.ts（計画のルート表とナビ項目の解決）が Vitest 側で固定している。
// 一覧・権限別の出し分け・エラー状態は Vitest（単体）で検証する——セッションは BFF（Keycloak）
// との往復で成立し、プレビューにはどちらも無いため、認証済みの導線は Playwright で実走できない。
test('unauthenticated visit to /admin/conversions redirects to /login', async ({ page }) => {
  await page.goto('/admin/conversions');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
