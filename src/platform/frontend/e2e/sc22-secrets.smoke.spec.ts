import { test, expect } from '@playwright/test';
import type { SecretItemGroupDto, SecretItemStatusDto } from '../src/lib/api/generated/bff.schemas';
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
  // ADR-0104 決定 2 (#1502): 供給元（BFF が同期先 ExternalSecret の有無から判定する）。
  supplySource: 'screen',
};

// 計画 ADR-0126 決定 1・3・4, IADR-0501 (#458 段 S3): 群「データソースの資格情報」。運用者には `writable: false` が返る。
const DATASOURCE_ID = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const operatorGroup: SecretItemGroupDto = {
  group: 'datasource-credentials',
  writable: false,
  members: [
    {
      memberId: DATASOURCE_ID,
      displayName: '社内 Wiki',
      kind: 'wiki',
      vaultPath: `datasource/${DATASOURCE_ID}`,
      propertyDetails: [{ name: 'apiToken', kind: 'value', sensitive: true }],
      status: 'notSet',
      currentVersion: null,
      lastUpdatedAt: null,
      lastUpdatedBy: null,
      supplySource: 'screen',
    },
  ],
};

test('unauthenticated visit to /admin/secrets redirects to /login', async ({ page }) => {
  await page.goto('/admin/secrets');

  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-22: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /secrets': [item],
      'GET /secrets/groups/datasource-credentials': operatorGroup,
    },
  });

  await page.goto('/admin/secrets');

  // ★ 陽性対照: 画面が描かれ、左ナビの項目も出る。値の列は無く、未設定が明示される。
  await expect(
    page.getByRole('heading', { name: '秘密情報・接続設定の管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: '秘密情報・接続設定の管理' })).toBeVisible();
  // 静的な項目の表に限って測る（同じ画面に群の表もあり、どちらにも「未設定」が出る。#458 段 S3）。
  const items = page.getByRole('table', { name: '秘密情報の項目の一覧' });
  await expect(items.getByRole('cell', { name: '未設定' })).toBeVisible();
  await expect(items.getByRole('cell', { name: '画面' })).toBeVisible();

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

// 計画 ADR-0126 決定 1・3 (#458 段 S3): SC-06 の導線（`?datasource=<ID>`）で開くと群の当該行が強調される。
// 🔴 **運用者は閲覧だけ** —— 群の表に「更新」は出ない（陽性対照: 静的な項目の「更新」は出る）。
test('SC-22: the data source credentials group is read-only for operators and highlights the linked row', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /secrets': [item],
      'GET /secrets/groups/datasource-credentials': operatorGroup,
    },
  });

  await page.goto(`/admin/secrets?datasource=${DATASOURCE_ID}`);

  const group = page.getByRole('table', { name: 'データソースの資格情報の一覧' });
  await expect(group).toBeVisible();
  await expect(group.getByTestId(`datasource-credential-row-${DATASOURCE_ID}`)).toHaveAttribute(
    'aria-current',
    'true',
  );
  await expect(group.getByText('実行時に取得・次の同期から効く')).toBeVisible();
  await expect(group.getByRole('button', { name: '更新' })).toHaveCount(0);
  await expect(
    page.getByRole('table', { name: '秘密情報の項目の一覧' }).getByRole('button', { name: '更新' }),
  ).toHaveCount(1);

  expectBffTrafficIsComplete(traffic);
});
