import { test, expect } from '@playwright/test';
import type { SecretItemStatusDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-22, FR-05, ADR-0095, ADR-0042 決定 2, IADR-0453 (#1411): 秘密情報・接続設定の管理（`/admin/secrets`）のスモーク。
//
// 🔴 **本画面は運用者・システム管理者に限る**（05_screens §SC-22）。権限外では `RequireRole` が `NotFound` を描き、
// 画面の存在を示さない（存在秘匿。IADR-0009）。**この出し分けは未認証のスモークでは踏まれない** —— ロールを与えて初めて分岐する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。

const item: SecretItemStatusDto = {
  item: 'llm-provider-credentials',
  vaultPath: 'msp/llm-provider-credentials',
  properties: ['anthropic-api-key', 'openai-api-key'],
  status: 'notSet',
  currentVersion: null,
  lastUpdatedAt: null,
  lastUpdatedBy: null,
};

test('unauthenticated visit to /admin/secrets redirects to /login', async ({ page }) => {
  await page.goto('/admin/secrets');

  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-22: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /secrets': [item] },
  });

  await page.goto('/admin/secrets');

  // ★ 陽性対照: 画面が描かれ、左ナビの項目も出る。値の列は無く、未設定が明示される。
  await expect(
    page.getByRole('heading', { name: '秘密情報・接続設定の管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: '秘密情報・接続設定の管理' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '未設定' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-22: other roles get the not-found page and never learn the screen exists', async ({
  page,
}) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 一覧を呼んでしまえば `unhandled` に載り、下で落ちる。
  const traffic = await installBffSession(page, { user: sessionUser(['platform-user']) });

  await page.goto('/admin/secrets');

  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '秘密情報・接続設定の管理' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '秘密情報・接続設定の管理' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /secrets');
  expectBffTrafficIsComplete(traffic);
});
