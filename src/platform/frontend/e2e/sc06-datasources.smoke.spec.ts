import { test, expect } from './support/cspGuard';
import type { Page } from '@playwright/test';
import type { DataSourceDto } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

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

// ───────────── M-078〜M-080（#2019 / E-14）: 登録フォーム・同期の異常・手動同期と無効化の権限 ─────────────
//
// 期待値は計画 05_screens §SC-06（既定属性 3 つの欄・department は空なら登録者の部門で埋まる・
// owner の写像先は実在を検証し通らない対は保存しない・同期異常は琥珀＋ラベルで連続失敗回数・上限・直近エラー・
// 手動同期は運用者に開き、登録・更新・無効化は管理者限定）から起こした。

function source(overrides: Partial<DataSourceDto>): DataSourceDto {
  return {
    id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
    name: '規程集',
    sourceType: 'filesystem',
    connectionUri: 'smb://fs01/share/規程集',
    status: 'active',
    lastSyncedAt: '2026-10-01T03:00:00Z',
    config: {},
    defaultAttributes: { confidentiality: 'internal' },
    createdAt: '2026-10-01T00:00:00Z',
    nextSyncAt: null,
    consecutiveFailureCount: 0,
    retryLimit: 5,
    lastSyncError: null,
    lastSyncErrorAt: null,
    ownerMappings: {},
    ...overrides,
  };
}
const rowOf = (page: Page, name: string) =>
  page
    .getByRole('table', { name: '登録済みデータソースの一覧' })
    .getByRole('row')
    .filter({ hasText: name });

test('M-079: 同期の連続失敗は回数と上限を文字で示し、上限に達したら「同期異常」と直近エラーを出す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /datasources': [
        source({ id: 'ok', name: '正常なソース' }),
        source({
          id: 'retry',
          name: '再試行中のソース',
          lastSyncedAt: null,
          consecutiveFailureCount: 2,
          lastSyncError: '接続がタイムアウトしました',
        }),
        source({
          id: 'broken',
          name: '止まったソース',
          lastSyncedAt: null,
          consecutiveFailureCount: 5,
          lastSyncError: '認証に失敗しました（値はマスク済み）',
        }),
      ],
    },
  });

  await page.goto('/admin/sources');
  await expect(rowOf(page, '正常なソース').getByText('同期済み')).toBeVisible();
  await expect(rowOf(page, '再試行中のソース').getByText('再試行中（2/5）')).toBeVisible();
  // ★ 上限（再試行上限 5）に達したら「同期異常」。分母は契約の retryLimit。
  const broken = rowOf(page, '止まったソース');
  await expect(broken.getByText('同期異常（5/5）')).toBeVisible();
  await expect(broken.getByText('認証に失敗しました（値はマスク済み）')).toBeVisible();
  // ★ 陰性対照: 正常なソースに異常の文言や直近エラーは出さない。
  await expect(rowOf(page, '正常なソース').getByText(/同期異常|再試行中/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-080: 運用者は手動同期を実行でき、無効化・既定属性の編集・ソース登録のボタンは持たない', async ({
  page,
}) => {
  const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      'GET /datasources': [source({})],
      [`POST /datasources/${id}/sync`]: reply(202, {}),
    },
  });

  await page.goto('/admin/sources');
  const row = rowOf(page, '規程集');
  await expect(row.getByRole('button', { name: '手動同期' })).toBeEnabled();
  // ★ 陰性対照: 破壊的操作（登録・更新・無効化）は管理者限定。運用者にはボタンごと無い。
  await expect(page.getByRole('button', { name: '無効化' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '既定属性' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '＋ ソース登録' })).toHaveCount(0);
  await expect(page.getByText('ソースの登録・無効化は管理者のみ実行できます')).toBeVisible();

  await row.getByRole('button', { name: '手動同期' }).click();
  await expect(
    page.getByRole('status').filter({ hasText: '同期をトリガしました。' }),
  ).toBeVisible();
  expect(traffic.calls.filter((c) => c.method !== 'GET').map((c) => c.key)).toEqual([
    `POST /datasources/${id}/sync`,
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-080: 管理者は無効化でき、無効化は 1 件の DELETE を送る', async ({ page }) => {
  const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
  let disabled = false;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /datasources': () => [source(disabled ? { status: 'disabled' } : {})],
      [`DELETE /datasources/${id}`]: () => {
        disabled = true;
        return reply(204, null);
      },
    },
  });

  await page.goto('/admin/sources');
  await rowOf(page, '規程集').getByRole('button', { name: '無効化' }).click();
  await expect(rowOf(page, '規程集').getByText('無効', { exact: true })).toBeVisible();
  // 無効化済みには無効化を出さない。
  await expect(rowOf(page, '規程集').getByRole('button', { name: '無効化' })).toHaveCount(0);
  expect(traffic.calls.filter((c) => c.method !== 'GET').map((c) => c.key)).toEqual([
    `DELETE /datasources/${id}`,
  ]);

  expectBffTrafficIsComplete(traffic);
});

test('M-078: 登録フォームは既定属性 3 つの欄を持ち、部門が空ならキーごと送らず、サーバが埋めた部門を一覧に出す', async ({
  page,
}) => {
  let created: DataSourceDto | null = null;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /datasources': () => (created ? [created] : []),
      'POST /datasources': (call) => {
        const body = call.body as { name: string; sourceType: string; connectionUri: string };
        // 登録した管理者の部門グループ（ちょうど 1 つ）から department をサーバが埋めた、という応答。
        created = source({
          name: body.name,
          sourceType: body.sourceType,
          connectionUri: body.connectionUri,
          lastSyncedAt: null,
          defaultAttributes: { confidentiality: 'internal', department: 'finance' },
        });
        return reply(201, created);
      },
    },
  });

  await page.goto('/admin/sources');
  await page.getByRole('button', { name: '＋ ソース登録' }).click();
  const form = page.getByRole('form', { name: 'データソース登録' });
  // ★ 既定属性 3 つ（confidentiality / department / lifecycle）の欄。
  await expect(form.getByLabel('既定の機密区分')).toBeVisible();
  await expect(form.getByLabel('既定の部門')).toHaveValue('');
  await expect(form.getByLabel('既定のライフサイクル状態')).toBeVisible();
  // 必須（名前・接続先 URI）が欠ければ登録できない。
  const submit = form.getByRole('button', { name: '登録する' });
  await expect(submit).toBeDisabled();

  await form.getByLabel('名前').fill('経理の共有');
  await form.getByLabel('接続先 URI').fill('smb://fs01/share/経理');
  await expect(submit).toBeEnabled();
  await submit.click();

  await expect(
    page.getByRole('status').filter({ hasText: 'データソースを登録しました。' }),
  ).toBeVisible();
  // ★ 部門が空なら department のキーを送らない（空文字も送らない）。
  const post = traffic.calls.find((c) => c.key === 'POST /datasources');
  const attrs =
    (post?.body as { defaultAttributes?: Record<string, string> }).defaultAttributes ?? {};
  expect(attrs).not.toHaveProperty('department');
  // ★ サーバが埋めた部門は表示され、管理者が確かめられる（計画: 埋まった値は表示され、直せる）。
  await expect(rowOf(page, '経理の共有').getByText('finance', { exact: true })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('M-078: 写像先の利用者が実在しない（400）と、サーバの理由を示して保存しない', async ({
  page,
}) => {
  const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /datasources': [source({})],
      [`PATCH /datasources/${id}`]: reply(400, {
        title: 'Validation failed',
        status: 400,
        errors: {
          ownerMappings: [
            '写像先の利用者が存在しません: nobody。利用者識別子（ログイン名）で指定してください。',
          ],
        },
      }),
    },
  });

  await page.goto('/admin/sources');
  await rowOf(page, '規程集').getByRole('button', { name: '既定属性' }).click();
  const form = page.getByRole('form', { name: '既定属性の編集' });
  await form.getByRole('button', { name: '＋ 写像を追加' }).click();
  await form.getByLabel('ソース側の利用者 1').fill('hr-system-nobody');
  await form.getByLabel('基盤の利用者 1').fill('nobody');
  await form.getByRole('button', { name: '更新する' }).click();

  await expect(page.getByRole('alert')).toContainText('写像先の利用者が存在しません: nobody');
  // ★ 保存できなかったので、フォームは開いたまま（入力を失わない）。成功の告知も出さない。
  await expect(form).toBeVisible();
  await expect(page.getByText('既定属性を更新しました。')).toHaveCount(0);
  const patch = traffic.calls.find((c) => c.key === `PATCH /datasources/${id}`);
  expect((patch?.body as { ownerMappings?: unknown }).ownerMappings).toEqual({
    'hr-system-nobody': 'nobody',
  });

  expectBffTrafficIsComplete(traffic);
});

// FR-04, SC-06 (#2019 / E-14・#2018 の追随): **既定の機密区分も用語集の表示名で出す**（公開 / 社内限 / 秘 / 取扱制限）。
// 期待値は計画の用語集「機密区分」から起こした。一覧の読み取り専用の表示と登録フォームの選択肢の 2 か所で
// 表示名を出し、**送る値は契約の値のまま**である。
test('既定の機密区分: 一覧と登録フォームは表示名で出し、要求には契約の値を載せる', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /datasources': [source({})],
      'POST /datasources': (call) => {
        const body = call.body as { name: string; sourceType: string; connectionUri: string };
        return reply(201, source({ id: 'b1c2d3e4-0000-4000-8000-000000000001', ...body }));
      },
    },
  });

  await page.goto('/admin/sources');
  // ★ 陽性対照: 行が描けている。そのうえで、生値（internal）ではなく表示名（社内限）が出る。
  const row = rowOf(page, '規程集');
  await expect(row).toBeVisible();
  await expect(row.getByText('社内限', { exact: true })).toBeVisible();
  await expect(row.getByText('internal', { exact: true })).toHaveCount(0);

  await page.getByRole('button', { name: '＋ ソース登録' }).click();
  const form = page.getByRole('form', { name: 'データソース登録' });
  const select = form.getByLabel('既定の機密区分');
  await expect(select.getByRole('option')).toHaveText(['公開', '社内限', '秘', '取扱制限']);
  await form.getByLabel('名前').fill('役員会の共有');
  await form.getByLabel('接続先 URI').fill('smb://fs01/share/役員会');
  await select.selectOption({ label: '秘' });
  await form.getByRole('button', { name: '登録する' }).click();
  await expect(
    page.getByRole('status').filter({ hasText: 'データソースを登録しました。' }),
  ).toBeVisible();
  const post = traffic.calls.find((c) => c.key === 'POST /datasources');
  expect(
    (post?.body as { defaultAttributes?: Record<string, string> }).defaultAttributes,
  ).toMatchObject({ confidentiality: 'confidential' });

  expectBffTrafficIsComplete(traffic);
});
