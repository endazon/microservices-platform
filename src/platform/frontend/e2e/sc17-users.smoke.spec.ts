import { test, expect } from './support/cspGuard';
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

// ───────────── M-106・M-107（#2020 / E-15）: 併任・機密区分上限の必須・無効化の確認・新規作成の不在 ─────────────
//
// 期待値は計画 05_screens §SC-17 から起こした。
// - ロール割当は必須・複数選択（併任可）。選択肢は定義済みロールだけ（画面は一覧の口から作る）。
// - ABAC 属性: 機密区分上限は**必須**（SC-09 に定義済みの値のみ）、タグは任意。
// - 部門は部門グループの所属の変更であり、属性 `department` を直接書かない（ADR-0116 決定 1）。
// - 無効化 → 全セッション即時失効（表示は「無効（全セッション失効）」）。
// - アカウントは人事システム連携で自動プロビジョニングし、**本画面から新規作成はしない**。

const ROLES = ['platform-user', 'knowledge-admin', 'platform-operator', 'platform-admin'];
const USER_DICTIONARY = [
  {
    id: '66666666-6666-4666-8666-666666666666',
    key: 'clearance',
    label: '機密区分上限',
    allowedValues: ['public', 'internal', 'confidential'],
    required: true,
    scope: 'user',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  },
  {
    id: '77777777-7777-4777-8777-777777777777',
    key: 'tags',
    label: 'タグ',
    allowedValues: ['経理'],
    required: false,
    scope: 'user',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  },
];
const editorHandlers = {
  'GET /admin/users': [user],
  'GET /admin/users/assignable-roles': ROLES,
  'GET /admin/authz/attributes': USER_DICTIONARY,
  'GET /admin/users/user-1/department': {
    departmentGroups: ['sales'],
    departmentAttribute: 'sales',
    choices: ['sales', 'dev'],
  },
};

test('M-106: ロールは定義済みの 4 種から併任でき、機密区分上限が無ければ保存せず、揃えば部門以外を送る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...editorHandlers,
      'PUT /admin/users/user-1/roles': (call) => ({
        ...user,
        roles: (call.body as { roles: string[] }).roles,
      }),
      'PUT /admin/users/user-1/attributes': (call) => ({
        ...user,
        attributes: (call.body as { attributes: Record<string, string> }).attributes,
      }),
    },
  });

  await page.goto('/admin/users');
  await page
    .getByRole('row')
    .filter({ hasText: 'hando.taro' })
    .getByRole('button', { name: '編集' })
    .click();
  const editor = page.getByTestId('permission-editor');
  await expect(editor).toBeVisible();

  // ★ 選択肢は定義済みのロールだけ（一覧の口が返した 4 種）。
  const roleGroup = editor.getByRole('group', { name: 'ロール割当（必須・複数選択可）' });
  await expect(roleGroup.getByRole('checkbox')).toHaveCount(4);
  // 併任: 運用者を足す（利用者は既に付いている）。
  await roleGroup.getByRole('checkbox', { name: 'platform-operator' }).check();

  // ★ 機密区分上限は必須: 未選択では保存の要求を出さず、理由を示す。
  await editor.getByRole('button', { name: '保存' }).click();
  await expect(editor.getByTestId('assignment-issues')).toContainText('機密区分上限は必須です。');
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  // 揃えて保存する。選択肢は定義済みの値だけ。
  const clearance = editor.getByLabel('機密区分上限 *');
  expect(await clearance.getByRole('option').allTextContents()).toEqual([
    '選択してください',
    'public',
    'internal',
    'confidential',
  ]);
  await clearance.selectOption('internal');
  await editor.getByRole('button', { name: '保存' }).click();
  await expect(editor.getByTestId('assignment-issues')).toHaveCount(0);
  await expect
    .poll(() =>
      traffic.calls
        .filter((c) => c.method === 'PUT')
        .map((c) => c.key)
        .sort(),
    )
    .toEqual(['PUT /admin/users/user-1/attributes', 'PUT /admin/users/user-1/roles']);

  const body = (key: string) => traffic.calls.find((c) => c.key === key)?.body;
  expect((body('PUT /admin/users/user-1/roles') as { roles: string[] }).roles.sort()).toEqual([
    'platform-operator',
    'platform-user',
  ]);
  // 属性は機密区分上限だけ（タグは任意なので未選択なら載らない）。部門は属性として書かない。
  expect(body('PUT /admin/users/user-1/attributes')).toEqual({
    attributes: { clearance: 'internal' },
  });
  expect(traffic.calls.map((c) => c.key)).not.toContain('PUT /admin/users/user-1/department');

  expectBffTrafficIsComplete(traffic);
});

test('M-106: 無効化は「全セッション失効」を告げる確認を挟み、やめれば送らず、確認すれば 1 回だけ送る', async ({
  page,
}) => {
  let disabled = false;
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...editorHandlers,
      'GET /admin/users': () => [{ ...user, enabled: !disabled }],
      'POST /admin/users/user-1/disable': () => {
        disabled = true;
        return { ...user, enabled: false };
      },
    },
  });

  await page.goto('/admin/users');
  const row = page.getByRole('row').filter({ hasText: 'hando.taro' });
  await expect(row.getByText('有効', { exact: true })).toBeVisible();
  await row.getByRole('button', { name: '編集' }).click();
  const editor = page.getByTestId('permission-editor');

  await editor.getByRole('button', { name: '無効化（全セッション失効）' }).click();
  const dialog = page.getByRole('alertdialog').or(page.getByRole('dialog'));
  await expect(dialog).toContainText('その利用者の全セッションが即座に失効し');
  await dialog.getByRole('button', { name: 'やめる' }).click();
  await expect(dialog).toHaveCount(0);
  const disables = () => traffic.calls.filter((c) => c.key === 'POST /admin/users/user-1/disable');
  expect(disables()).toHaveLength(0);

  await editor.getByRole('button', { name: '無効化（全セッション失効）' }).click();
  await dialog.getByRole('button', { name: '無効化する' }).click();
  await expect(row.getByText('無効（全セッション失効）')).toBeVisible();
  expect(disables()).toHaveLength(1);

  expectBffTrafficIsComplete(traffic);
});

test('M-107: 本画面にはアカウントを新規作成する操作が無く、作成は人事連携による旨を示す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: adminHandlers,
  });

  await page.goto('/admin/users');
  // 陽性対照: 一覧の行と、その行の操作（編集）は描けている。
  const row = page.getByRole('row').filter({ hasText: 'hando.taro' });
  await expect(row.getByRole('button', { name: '編集' })).toBeVisible();

  // ★ 本文領域（main。共通シェルのヘッダ・ナビは除く）の全ボタンの集合を完全一致で見る
  // （作成・追加・招待の操作がどんな文言で増えても赤になる）。
  // 並べ替えの見出しボタン（ユーザー・状態）と、行の「編集」だけである。
  const names = (await page.getByRole('main').getByRole('button').allTextContents()).map((n) =>
    n.trim(),
  );
  expect(names).toEqual(['ユーザー', '状態', '編集']);
  await expect(page.getByTestId('users-help')).toContainText('この画面からは作成できません');
  expect(traffic.calls.filter((c) => c.method !== 'GET')).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
