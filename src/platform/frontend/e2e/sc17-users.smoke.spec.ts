import { test, expect } from '@playwright/test';
import type { PlatformUserDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-17, UC-05, FR-05, FR-09, ADR-0026 (#1099): ユーザーアカウント管理（`/admin/users`）のスモーク。
//
// 🔴 **本画面は platform-admin 限定である**（05_screens §共通シェル / §SC-17。運用者も不可）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない** —— ロールを与えて初めて分岐する。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下に居る。#918）。
// ルートの実在は、下の「管理者で開く」本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。

const user: PlatformUserDto = {
  id: 'user-1',
  username: 'hando.taro',
  displayName: 'ハンドウ タロウ',
  enabled: true,
  roles: ['platform-user'],
  attributes: { department: '営業部' },
};

const adminHandlers = {
  'GET /admin/users': [user],
  'GET /admin/users/assignable-roles': ['platform-user', 'platform-admin'],
  'GET /admin/authz/attributes': [],
};

test('unauthenticated visit to /admin/users redirects to /login', async ({ page }) => {
  await page.goto('/admin/users');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-17: a platform administrator reaches the screen and its navigation entry', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: adminHandlers,
  });

  await page.goto('/admin/users');

  // ★ 陽性対照: 画面が描かれ、左ナビ「ユーザー管理」も出る（05_screens §共通シェル）。
  await expect(
    page.getByRole('heading', { name: 'ユーザーアカウント管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'ユーザー管理' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'hando.taro' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-17: a non-administrator gets the same not-found page and never learns the screen exists', async ({
  page,
}) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼んでしまえば `unhandled` に載り、
  // 下の `expectBffTrafficIsComplete` が落ちる —— 「権限が無いのに取りに行った」を検出する。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/users');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'ユーザーアカウント管理' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: 'ユーザー管理' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/users');
  expectBffTrafficIsComplete(traffic);
});
