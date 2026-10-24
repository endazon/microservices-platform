import { test, expect } from './support/cspGuard';
import type { EffectiveToolsView, McpClientView } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-12, UC-09, FR-16, ADR-0024 (#1099): MCP クライアント登録管理（`/admin/mcp-clients`）のスモーク。
//
// 🔴 **本画面は platform-admin 限定である**（05_screens §共通シェル / §SC-12。運用者も不可）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない** —— ロールを与えて初めて分岐する。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下に居る。#918）。
// ルートの実在は、下の「管理者で開く」本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。

const client: McpClientView = {
  id: 'client-1',
  clientId: 'agent-alpha',
  displayName: '社内エージェント α',
  kind: 'service-account',
  enabled: true,
  attributes: { department: 'sales' },
  egressTier: 'self-hosted',
  registeredAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

const tools: EffectiveToolsView = {
  version: 3,
  tools: [
    {
      name: 'search_documents',
      service: 'SearchService',
      description: '横断検索',
      requiredScope: 'knowledge.read',
      egressClass: 'internal',
    },
  ],
  drifts: [],
};

const adminHandlers = {
  'GET /admin/mcp-clients': [client],
  'GET /admin/mcp-clients/tools': tools,
  'GET /admin/authz/attributes': [],
};

test('unauthenticated visit to /admin/mcp-clients redirects to /login', async ({ page }) => {
  await page.goto('/admin/mcp-clients');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-12: a platform administrator reaches the screen and its navigation entry', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: adminHandlers,
  });

  await page.goto('/admin/mcp-clients');

  // ★ 陽性対照: 画面が描かれ、左ナビ「MCP管理」も出る（05_screens §共通シェル）。
  await expect(
    page.getByRole('heading', { name: 'MCP クライアント登録管理', level: 1 }),
  ).toBeVisible();
  await expect(page.getByRole('link', { name: 'MCP管理' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'agent-alpha' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-12: a non-administrator gets the same not-found page and never learns the screen exists', async ({
  page,
}) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼んでしまえば `unhandled` に載り、
  // 下の `expectBffTrafficIsComplete` が落ちる —— 「権限が無いのに取りに行った」を検出する。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/mcp-clients');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'MCP クライアント登録管理' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: 'MCP管理' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/mcp-clients');
  expectBffTrafficIsComplete(traffic);
});

// ───────────── M-101・M-102（#2020 / E-15）: 登録（有人／無人）・無効化・部分集合の拒否・secret の一度だけの表示 ─────────────
//
// 期待値は計画 05_screens §SC-12 から起こした。
// - クライアント種別は 有人（Authorization Code + PKCE）／無人（Client Credentials）の 2 値。有人はリダイレクト URI が必須。
// - 無人の ABAC 属性は**登録者自身の集合の部分集合**。判定は後段が行い、画面は事前に判定しない。
//   後段の拒否応答に**どの値が外れたか**が含まれ、画面はそれを表示する（ADR-0062）。
// - 無効化 → 即時に接続拒否（状態が「無効」になる）。
// - 無人の secret は登録・再発行の直後に**一度だけ**表示する。再発行は確認を挟む（ADR-0134 決定 2）。

const ATTRIBUTE_DICTIONARY = [
  {
    id: '55555555-5555-4555-8555-555555555555',
    key: 'clearance',
    label: '取扱可能区分',
    allowedValues: ['public', 'internal', 'confidential', 'restricted'],
    required: true,
    scope: 'user',
    createdAt: '2026-10-01T00:00:00Z',
    updatedAt: '2026-10-01T00:00:00Z',
  },
];

test('M-101: 有人の登録は種別とリダイレクト URI を送り、無効化は 1 件の POST で状態を「無効」にする', async ({
  page,
}) => {
  let clients: McpClientView[] = [client];
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...adminHandlers,
      'GET /admin/mcp-clients': () => clients,
      'GET /admin/authz/attributes': ATTRIBUTE_DICTIONARY,
      'POST /admin/mcp-clients': (call) => {
        const body = call.body as { clientId: string; displayName: string; kind: string };
        const created: McpClientView = {
          ...client,
          id: 'client-2',
          clientId: body.clientId,
          displayName: body.displayName,
          kind: 'interactive',
          attributes: {},
        };
        clients = [...clients, created];
        return reply(201, { ...created, clientSecret: null });
      },
      'POST /admin/mcp-clients/agent-alpha/disable': () => {
        clients = clients.map((c) => (c.clientId === 'agent-alpha' ? { ...c, enabled: false } : c));
        return { ...client, enabled: false };
      },
    },
  });

  await page.goto('/admin/mcp-clients');
  // ★ 種別は 2 値だけ（有人＝PKCE／無人＝Client Credentials）。
  const kind = page.getByRole('combobox', { name: 'クライアント種別' });
  // 陽性対照: 一覧と登録フォームが描けてから数える（描画前に数えると空振りする）。
  await expect(page.getByRole('cell', { name: 'agent-alpha' })).toBeVisible();
  expect(await kind.getByRole('option').allTextContents()).toEqual([
    '有人（Authorization Code + PKCE）',
    '無人（Client Credentials）',
  ]);
  await kind.selectOption('interactive');
  await page.getByLabel('クライアント ID').fill('desk-agent');
  await page.getByLabel('表示名').fill('デスクのエージェント');
  await page.getByLabel('リダイレクト URI（1 行に 1 件）').fill('http://127.0.0.1/callback');
  await page.getByRole('button', { name: '登録', exact: true }).click();
  await expect(page.getByRole('cell', { name: 'desk-agent' })).toBeVisible();
  // 有人は secret を持たない（一度だけの表示も出ない）。
  await expect(page.getByTestId('issued-secret')).toHaveCount(0);
  expect(traffic.calls.find((c) => c.key === 'POST /admin/mcp-clients')?.body).toMatchObject({
    clientId: 'desk-agent',
    displayName: 'デスクのエージェント',
    kind: 'interactive',
    redirectUris: ['http://127.0.0.1/callback'],
  });

  // ★ 無効化: 1 件の POST を送り、行の状態が「無効（即時接続拒否）」になる。
  const alpha = page.getByRole('row').filter({ hasText: 'agent-alpha' });
  await alpha.getByRole('button', { name: '無効化' }).click();
  await expect(alpha.getByText('無効（即時接続拒否）')).toBeVisible();
  expect(
    traffic.calls.filter((c) => c.key.startsWith('POST /admin/mcp-clients/')).map((c) => c.key),
  ).toEqual(['POST /admin/mcp-clients/agent-alpha/disable']);

  expectBffTrafficIsComplete(traffic);
});

test('M-101: 無人の属性が登録者の部分集合から外れると 400 になり、外れた値を画面に示して登録しない', async ({
  page,
}) => {
  const outOfSubset = '割り当てられない属性があります（登録者が持たない値）: clearance=restricted';
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...adminHandlers,
      'GET /admin/authz/attributes': ATTRIBUTE_DICTIONARY,
      'POST /admin/mcp-clients': reply(400, {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { attributes: [outOfSubset] },
      }),
    },
  });

  await page.goto('/admin/mcp-clients');
  await page.getByLabel('クライアント種別').selectOption('service-account');
  await page.getByLabel('クライアント ID').fill('batch-agent');
  await page.getByLabel('表示名').fill('夜間バッチ');
  // 画面は事前に判定しない: 4 区分すべてが選べる（定義済みの値だけ）。
  const value = page.getByTestId('attribute-assignment').getByLabel('値');
  await page.getByTestId('attribute-assignment').getByLabel('属性').selectOption('clearance');
  expect(await value.getByRole('option').allTextContents()).toEqual([
    '選択してください',
    'public',
    'internal',
    'confidential',
    'restricted',
  ]);
  await value.selectOption('restricted');
  await page.getByRole('button', { name: '属性を追加' }).click();
  await page.getByRole('button', { name: '登録', exact: true }).click();

  // ★ どの値が外れたかを示す。行は増えず、secret も出ない。
  await expect(page.getByTestId('registration-error')).toContainText('clearance=restricted');
  await expect(page.getByRole('cell', { name: 'batch-agent' })).toHaveCount(0);
  await expect(page.getByTestId('issued-secret')).toHaveCount(0);
  expect(traffic.calls.find((c) => c.key === 'POST /admin/mcp-clients')?.body).toMatchObject({
    kind: 'service-account',
    attributes: { clearance: 'restricted' },
  });

  expectBffTrafficIsComplete(traffic);
});

test('M-102: 無人の secret は登録の直後に一度だけ表示し、閉じると消える。再発行は確認を挟み、取り消せば送らない', async ({
  page,
}) => {
  let clients: McpClientView[] = [];
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      ...adminHandlers,
      'GET /admin/mcp-clients': () => clients,
      'GET /admin/authz/attributes': ATTRIBUTE_DICTIONARY,
      'POST /admin/mcp-clients': () => {
        const created: McpClientView = {
          ...client,
          clientId: 'batch-agent',
          displayName: '夜間バッチ',
          attributes: { clearance: 'internal' },
        };
        clients = [created];
        return reply(201, { ...created, clientSecret: 'first-secret-value' });
      },
      'POST /admin/mcp-clients/batch-agent/reissue-secret': {
        clientId: 'batch-agent',
        clientSecret: 'second-secret-value',
      },
    },
  });

  await page.goto('/admin/mcp-clients');
  await page.getByLabel('クライアント種別').selectOption('service-account');
  await page.getByLabel('クライアント ID').fill('batch-agent');
  await page.getByLabel('表示名').fill('夜間バッチ');
  await page.getByTestId('attribute-assignment').getByLabel('属性').selectOption('clearance');
  await page.getByTestId('attribute-assignment').getByLabel('値').selectOption('internal');
  await page.getByRole('button', { name: '属性を追加' }).click();
  await page.getByRole('button', { name: '登録', exact: true }).click();

  // ★ 一度だけの表示: 値と「今回だけ」の断りが出る。閉じると消え、一覧にも値は出ない。
  const issued = page.getByTestId('issued-secret');
  await expect(issued.getByTestId('issued-secret-value')).toHaveText('first-secret-value');
  await expect(issued).toContainText('表示できるのは今回だけです');
  await issued.getByRole('button', { name: '閉じる' }).click();
  await expect(issued).toHaveCount(0);
  await expect(page.getByText('first-secret-value')).toHaveCount(0);

  // ★ 再発行は確認を挟む。取り消すと何も送らない。
  const row = page.getByRole('row').filter({ hasText: 'batch-agent' });
  await row.getByRole('button', { name: 'secret を再発行' }).click();
  const confirm = page.getByTestId('reissue-confirmation');
  await expect(confirm).toContainText('いまの secret はただちに使えなくなります');
  await confirm.getByRole('button', { name: '取消' }).click();
  await expect(confirm).toHaveCount(0);
  const reissues = () =>
    traffic.calls.filter((c) => c.key === 'POST /admin/mcp-clients/batch-agent/reissue-secret');
  expect(reissues()).toHaveLength(0);

  // 確認すると 1 回だけ送り、新しい値を一度だけ表示する。
  await row.getByRole('button', { name: 'secret を再発行' }).click();
  await page
    .getByTestId('reissue-confirmation')
    .getByRole('button', { name: '再発行する' })
    .click();
  await expect(page.getByTestId('issued-secret-value')).toHaveText('second-secret-value');
  expect(reissues()).toHaveLength(1);

  expectBffTrafficIsComplete(traffic);
});
