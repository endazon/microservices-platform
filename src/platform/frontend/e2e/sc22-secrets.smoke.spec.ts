import { test, expect } from './support/cspGuard';
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

// ───────────── M-111・M-112（#2020 / E-15）: 書き込み専用・確認入力 2 回・再起動の確認・供給元 3 値・一括再投入の不在 ─────────────
//
// 期待値は計画 05_screens §SC-22 から起こした。
// - 項目の一覧の列は 項目名／用途／最終更新日時／最終更新者／操作（＋供給元）。**値の列を置かない。**
// - 供給元は「画面／画面以外／確認できない」の 3 値で出す（不明を 2 値のどちらかへ寄せない。ADR-0110 決定 1）。
// - 値が入っていない項目は「未設定」と出す（空欄と「設定済みだが読み出せない」を区別する）。
// - 値は書き込み専用（マスク入力）。確認入力を 2 度求め、貼り付けの誤りを保存前に捕まえる。
// - 書き込みの確認は消費側の再起動の確認を兼ねる（確認して初めて送る。ADR-0110 決定 3）。
// - 更新は 1 項目ずつ。**一括再投入のボタンを置かない**（ADR-0095 決定 4）。

const SECRET = 'sk-e2e-not-a-real-key';
const threeItems: SecretItemStatusDto[] = [
  {
    ...item,
    status: 'set',
    currentVersion: 3,
    lastUpdatedAt: '2026-10-20T01:00:00Z',
    lastUpdatedBy: 'ops.hanako',
    supplySource: 'screen',
  },
  {
    ...item,
    item: 'smtp-relay',
    vaultPath: 'msp/smtp-relay',
    properties: ['password'],
    supplySource: 'git',
  },
  {
    ...item,
    item: 'object-storage',
    vaultPath: 'msp/object-storage',
    properties: ['secret-key'],
    status: 'unavailable',
    supplySource: 'unknown',
  },
];
const secretHandlers = {
  'GET /secrets': threeItems,
  'GET /secrets/groups/datasource-credentials': operatorGroup,
};

test('M-111: 一覧に値の列は無く、未設定・取得できないを区別し、供給元を 3 値で出す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: secretHandlers,
  });

  await page.goto('/admin/secrets');
  const table = page.getByRole('table', { name: '秘密情報の項目の一覧' });
  await expect(table.getByRole('row')).toHaveCount(4);

  // ★ 列の全集合（値の列が無い）。
  const headers = (await table.getByRole('columnheader').allTextContents()).map((h) => h.trim());
  expect(headers.map((h) => h.replace(/並べ替え.*$/, '').trim())).toEqual([
    '項目名',
    '用途',
    '最終更新日時',
    '最終更新者',
    '供給元',
    '操作',
  ]);
  expect(headers.filter((h) => h.includes('値'))).toEqual([]);

  // ★ 状態: 設定済み／未設定／取得できない を描き分ける。
  const rowOf = (name: string) => table.getByRole('row').filter({ hasText: name });
  await expect(rowOf('llm-provider-credentials').getByText('設定済み')).toBeVisible();
  await expect(rowOf('llm-provider-credentials').getByText('ops.hanako')).toBeVisible();
  await expect(rowOf('smtp-relay').getByText('未設定')).toBeVisible();
  await expect(rowOf('object-storage').getByText('取得できない')).toBeVisible();
  // ★ 供給元の 3 値。
  await expect(rowOf('llm-provider-credentials').getByText('画面', { exact: true })).toBeVisible();
  await expect(rowOf('smtp-relay').getByText('画面以外', { exact: true })).toBeVisible();
  await expect(rowOf('object-storage').getByText('確認できない', { exact: true })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('M-111: 値はマスク入力で、確認入力が違えば送らず、再起動の確認を経て 1 回だけ書き、値を画面に出さない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: {
      ...secretHandlers,
      'PUT /secrets/llm-provider-credentials': {
        item: 'llm-provider-credentials',
        property: 'anthropic-api-key',
        version: 4,
        updatedAt: '2026-10-24T01:00:00Z',
        syncRequested: true,
      },
    },
  });

  await page.goto('/admin/secrets');
  await page
    .getByRole('table', { name: '秘密情報の項目の一覧' })
    .getByRole('row')
    .filter({ hasText: 'llm-provider-credentials' })
    .getByRole('button', { name: '更新' })
    .click();
  const form = page.getByTestId('secret-update-form');
  await form.getByLabel('更新するプロパティ').selectOption('anthropic-api-key');

  // ★ 書き込み専用のマスク入力が 2 つ（値と確認）。
  const value = form.getByLabel('新しい値', { exact: true });
  const confirmation = form.getByLabel('新しい値（確認のためもう一度）');
  await expect(value).toHaveAttribute('type', 'password');
  await expect(confirmation).toHaveAttribute('type', 'password');

  // ★ 確認入力が違えば送れない（不一致を示し、更新のボタンを押せない）。
  const submit = form.getByRole('button', { name: 'このプロパティを更新する' });
  await value.fill(SECRET);
  await confirmation.fill(`${SECRET}x`);
  await expect(form.getByTestId('secret-confirmation-mismatch')).toBeVisible();
  await expect(submit).toBeDisabled();
  const puts = () => traffic.calls.filter((c) => c.method === 'PUT');
  expect(puts()).toHaveLength(0);

  // ★ 揃えると、再起動の確認が出る。やめれば送らない。
  await confirmation.fill(SECRET);
  await expect(form.getByTestId('secret-confirmation-mismatch')).toHaveCount(0);
  await submit.click();
  const dialog = page.getByRole('alertdialog').or(page.getByRole('dialog'));
  await expect(dialog.getByTestId('secret-restart-confirm')).toBeVisible();
  await dialog.getByRole('button', { name: 'やめる' }).click();
  await expect(dialog).toHaveCount(0);
  expect(puts()).toHaveLength(0);

  // 確認して書く。1 回だけ送り、送った値は画面のどこにも出ない。
  await submit.click();
  await dialog.getByRole('button', { name: '書き込む' }).click();
  await expect(page.getByTestId('secret-update-done')).toBeVisible();
  expect(puts().map((c) => c.key)).toEqual(['PUT /secrets/llm-provider-credentials']);
  expect(puts()[0]?.body).toMatchObject({ property: 'anthropic-api-key', value: SECRET });
  await expect(page.getByText(SECRET)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-112: 一括再投入の操作は無く、更新は項目ごとのボタンだけである', async ({ page }) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-operator']),
    handlers: secretHandlers,
  });

  await page.goto('/admin/secrets');
  const table = page.getByRole('table', { name: '秘密情報の項目の一覧' });
  // 陽性対照: 項目ごとの「更新」は 3 行ぶん出ている。
  await expect(table.getByRole('button', { name: '更新' })).toHaveCount(3);

  // ★ 画面の全ボタンに、一括・全件・再投入の操作が 1 つも無い。
  const names = (await page.getByRole('main').getByRole('button').allTextContents()).map((n) =>
    n.trim(),
  );
  expect(names.filter((n) => /一括|全件|すべて|再投入|まとめて/.test(n))).toEqual([]);
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
