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

// SC-06 主要素, 計画 ADR-0126 決定 1 (#458 段 S3): 各行から SC-22 の群の当該項目への導線（「認証情報を設定」）。
// 🔴 本画面に入力欄は置かない。導線で SC-22 へ移り、群の当該行が強調される。
test('SC-06: an admin follows the credentials link to the SC-22 group item', async ({ page }) => {
  const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /datasources': [
        {
          id,
          name: '社内 Wiki',
          sourceType: 'wiki',
          connectionUri: 'https://wiki.example.test',
          status: 'active',
          lastSyncedAt: null,
          config: {},
          defaultAttributes: { confidentiality: 'internal' },
          createdAt: '2026-10-01T00:00:00Z',
        },
      ],
      'GET /secrets': [],
      'GET /secrets/groups/datasource-credentials': {
        group: 'datasource-credentials',
        writable: true,
        members: [
          {
            memberId: id,
            displayName: '社内 Wiki',
            kind: 'wiki',
            vaultPath: `datasource/${id}`,
            propertyDetails: [{ name: 'apiToken', kind: 'value', sensitive: true }],
            status: 'notSet',
            currentVersion: null,
            lastUpdatedAt: null,
            lastUpdatedBy: null,
            supplySource: 'screen',
          },
        ],
      },
    },
  });

  await page.goto('/admin/sources');
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await page.getByRole('link', { name: '認証情報を設定' }).click();

  await expect(page).toHaveURL(new RegExp(`/admin/secrets\\?datasource=${id}$`));
  await expect(page.getByTestId(`datasource-credential-row-${id}`)).toHaveAttribute(
    'aria-current',
    'true',
  );
  await expect(page.getByTestId('datasource-credential-update-form')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});
