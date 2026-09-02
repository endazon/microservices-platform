import { test, expect } from '@playwright/test';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-06 (#503 / #1139): データソース管理（`/admin/sources`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧・登録・同期・エラー状態は Vitest（単体）が引き続き担う。

test('unauthenticated visit to /admin/sources redirects to /login', async ({ page }) => {
  await page.goto('/admin/sources');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-06: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /datasources': [] },
  });

  await page.goto('/admin/sources');

  // ★ 陽性対照: 画面が描かれ、左ナビ「データソース」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: 'データソース', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'データソース' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-06: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/sources');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'データソース', level: 1 })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'データソース' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /datasources');
  expectBffTrafficIsComplete(traffic);
});
