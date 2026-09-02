import { test, expect } from '@playwright/test';

// SC-21 (#918): スクリーンレベル・スモーク（バックエンド不要）。
// /ai-suggestions は認証必須（RequireAuth）配下。未認証で開くと /login へ誘導される。
//
// 🔴 **本テストが固定するのは「認証ガードが先に効くこと」だけである。ルートの実在は固定できない。**
// 実測（#918 でパスを改名する変異を当てて確認した）: 未知のパスの受け皿（catchAllRoute）は
// `RequireAuth` 配下の shellRoute の子であるため、**ルートが存在しなくても未認証なら /login へ行く**。
// ルートの実在は router.test.ts（計画のルート表とナビ項目の解決）が Vitest 側で固定している。
//
// 🔴 **一覧の中身・フィルタ・一括承認の不在・SC-03 への導線も Vitest（単体）で検証する。**
// **本 spec は認証済みの導線を踏んでいない。踏めないからではない**（#1099 が実測で覆した）。
// セッションを与える土台は `support/bffSession.ts` にあり、SC-12 / SC-17 / SC-19 / SC-20 が使う。
// 本画面へ広げるかは #1139 が判断する。
test('unauthenticated visit to /ai-suggestions redirects to /login', async ({ page }) => {
  await page.goto('/ai-suggestions?state=pending');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});
