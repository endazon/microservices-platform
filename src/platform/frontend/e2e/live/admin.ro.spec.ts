// NFR / FR-01 / FR-05 / FR-06 / FR-09 / FR-10 / FR-12 / FR-15 / FR-16 / NFR-18 / SC-05 / SC-06 / SC-07 / SC-09 / SC-10 / SC-11 / SC-12 / SC-17 / SC-22 /
// IADR-0035 / IADR-0337 / IADR-0627（#2029 / E-42）: live-ro の**管理・運用の画面の到達**。網羅表（#2013）の M-075・M-081・M-086・M-090・M-091・
// M-096・M-099・M-103・M-108・M-113 を、`e2e-operator` / `e2e-admin` / `e2e-user` の storageState で流す。
//
// - 🔴 読み取りだけ（画面を開く・画面が送る GET・同じ cookie での GET）。登録・更新・削除・同期・再変換はしない（L3。E-52）。
// - 各画面で、本体の見出しが出ることと、画面が引く BFF の GET が 200 で返ることを同じ表示で見る（`support/screens.ts`）。
// - 権限の否定系は「隣の役割」で見る（IADR-0337）。管理者限定（SC-09/12/17）の陰性は operator、運用者の画面（SC-11）の陰性は user。
//   UI の NotFound と、BFF の実効境界（AdminOnly は 403、構成ビューアは存在秘匿の 404）を同じ役割で見る。
// - 文言は ja のカタログから引く（`support/catalog.ts`）。
import { readFileSync } from 'node:fs';
import { expect, test, type Page, type Response } from '@playwright/test';
import { ja } from './support/catalog';
import { parseLiveEnv, ROLES, stateFile, type Role } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { allOk, expectHidden, openScreen } from './support/screens';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);
const as = (role: Role) => ({ storageState: stateFile(cfg.authDir, role) });

/** ABAC の seed の宣言（ポリシーの名前の単一情報源）。 */
const ABAC_SEED = JSON.parse(
  readFileSync(
    new URL('../../../../../deploy/local/abac-seed/policies.json', import.meta.url),
    'utf8',
  ),
) as { policies: { name: string }[] };
/** 検索用 seed の宣言（文書名の単一情報源。IADR-0627）。 */
const SEARCH_SEED = JSON.parse(
  readFileSync(
    new URL('../../../../../deploy/local/search-seed/documents.json', import.meta.url),
    'utf8',
  ),
) as { documents: { title: string }[] };

const SEEDS_REASON =
  'E2E_SEARCH_HITS=1 が無い（seed 文書が在る使い捨てのスタックでだけ、行の操作の不在を判定する。IADR-0627）';

/** JSON の中に、名前が `key` の鍵が（どの深さにも）在るか。 */
function hasKey(value: unknown, key: string): boolean {
  if (Array.isArray(value)) return value.some((v) => hasKey(v, key));
  if (value !== null && typeof value === 'object')
    return Object.entries(value).some(([k, v]) => k === key || hasKey(v, key));
  return false;
}

/**
 * SC-22 の一覧の応答を、設計にある 2 つの形のどちらかとして確かめる（IADR-0453 決定 5）。どちらでもなければ落とす。
 * - `listed`: 200。応答のどの深さにも `value` の鍵が無い（値は書き込み専用）。
 * - `store-not-deployed`: 503。problem の type が `vault-not-configured` / `vault-unavailable` で、画面がその旨を出す。
 */
async function secretsOutcome(page: Page, res: Response): Promise<string> {
  if (res.status() === 200) {
    expect(hasKey(await res.json(), 'value'), '応答の value').toBe(false);
    return 'listed';
  }
  expect(res.status(), 'GET /bff/secrets').toBe(503);
  const problem = (await res.json()) as { type?: string };
  expect(problem.type ?? '', 'problem の type').toMatch(/\/vault-(not-configured|unavailable)$/);
  await expect(
    page.getByText(
      ja(
        '秘密情報の保管先（Vault）が構成されていないか、接続できません。保管先の稼働を確認してください。',
      ),
      { exact: true },
    ),
  ).toBeVisible();
  return 'store-not-deployed';
}

/** SC-05 の破壊操作・作成の押しボタン（L1 の `sc05-documents.smoke.spec.ts` の M-073 と同じ集合）。 */
const DOCUMENT_WRITE_BUTTONS = ['編集', '公開', 'アーカイブ', '削除', '＋ 新規登録'];

test.describe('e2e-operator の管理・運用の画面', () => {
  test.use(as('operator'));

  // SC-05, FR-06, IADR-0337: M-075。
  test(
    'M-075: 文書管理に到達し、一覧の取得は 200 で、登録と操作の列が無い',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/documents'];
      const { statuses } = await openScreen(page, '/admin/documents', '文書一覧', apis);
      expect(statuses).toEqual(allOk(apis));
      await expect(page.getByRole('button', { name: ja('＋ 新規登録'), exact: true })).toHaveCount(
        0,
      );
      await expect(page.getByRole('columnheader', { name: ja('操作') })).toHaveCount(0);
    },
  );

  // SC-05, FR-06: M-075（seed あり）。行が在る状態で、行の破壊操作が 1 つも無い（0 件の一覧で緑にしない）。
  test(
    'M-075: seed 文書の行が一覧に出ていても、編集・公開・アーカイブ・削除の押しボタンが 1 つも無い',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      test.skip(!cfg.searchHits, SEEDS_REASON);
      const apis = ['/bff/documents'];
      const { statuses } = await openScreen(page, '/admin/documents', '文書一覧', apis);
      expect(statuses).toEqual(allOk(apis));
      // 陽性対照: seed 文書の行が出ている（operator は clearance=internal なので 2 件とも見える）。
      for (const { title } of SEARCH_SEED.documents)
        await expect(page.getByText(title, { exact: true })).not.toHaveCount(0);
      for (const name of DOCUMENT_WRITE_BUTTONS)
        await expect(page.getByRole('button', { name: ja(name), exact: true })).toHaveCount(0);
    },
  );

  // SC-06, FR-01, FR-02: M-081。
  test(
    'M-081: データソースの一覧に到達し、一覧の取得が 200 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/datasources'];
      const { statuses } = await openScreen(page, '/admin/sources', 'データソース', apis);
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-07, FR-12: M-086。
  test(
    'M-086: 変換ジョブの一覧に到達し、一覧の取得が 200 になる',
    { tag: ['@p2', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/conversion/jobs'];
      const { statuses } = await openScreen(
        page,
        '/admin/conversions',
        '変換ジョブ（pandoc＋LLM）',
        apis,
      );
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-09 / SC-12 / SC-17, FR-09, IADR-0035, IADR-0337: M-091（＋同じ管理者限定の 2 画面）。
  test(
    'M-091: 管理者限定の ABAC 設定・MCP 管理・ユーザー管理は見つかりませんでしたになり、BFF の取得は 403 になる',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      await expectHidden(page, '/admin/abac', '管理者設定（ABAC）');
      await expectHidden(page, '/admin/mcp-clients', 'MCP クライアント登録管理');
      await expectHidden(page, '/admin/users', 'ユーザーアカウント管理');
      const apis = ['/bff/admin/authz/policies', '/bff/admin/mcp-clients', '/bff/admin/users'];
      const statuses: Record<string, number> = {};
      for (const api of apis)
        statuses[api] = (await page.request.get(api, { failOnStatusCode: false })).status();
      expect(statuses).toEqual(Object.fromEntries(apis.map((a) => [a, 403])));
    },
  );

  // SC-10, FR-10: M-096。
  test(
    'M-096: 運用ダッシュボードに到達し、集計の取得が 200 で、KPI（検索総数）が描かれる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/dashboard/summary'];
      const { statuses } = await openScreen(page, '/admin/ops', '運用ダッシュボード', apis);
      expect(statuses).toEqual(allOk(apis));
      await expect(page.getByText(ja('検索総数'), { exact: true })).toBeVisible();
    },
  );

  // SC-11, FR-15: M-099（陽性側）。
  test(
    'M-099: 構成ビューアに到達し、実効構成の取得が 200 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/admin/config'];
      const { statuses } = await openScreen(page, '/admin/config-viewer', '実効構成', apis);
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-22, NFR-18, IADR-0453 決定 5: M-113。値は一覧にも応答にも載らない（書き込み専用）。保管先（Vault）を配備していない構成では、
  // 一覧の取得は設計どおり 503（problem の type が vault-not-configured / vault-unavailable）で、画面はその旨を出す。使い捨てのスタック
  // （e2e-live.yml）は VAULT を立てないのでこちらの形になる。どちらの形でも値は出ない。どちらでもない応答は落とす。
  test(
    'M-113: 秘密情報の管理に到達し、一覧は 200（値の鍵も値の列も無い）か、保管先が無い構成の 503（その旨を表示）のどちらかになる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }, testInfo) => {
      const apis = ['/bff/secrets'];
      const { statuses, bodies } = await openScreen(
        page,
        '/admin/secrets',
        '秘密情報・接続設定の管理',
        apis,
      );
      const outcome = await secretsOutcome(page, bodies.get('/bff/secrets')!);
      testInfo.annotations.push({ type: 'SC-22 の一覧', description: outcome });
      expect([200, 503], `GET /bff/secrets（${outcome}）`).toContain(statuses['/bff/secrets']);
      await expect(page.getByRole('columnheader', { name: ja('値'), exact: true })).toHaveCount(0);
    },
  );
});

test.describe('e2e-admin の管理者限定の画面', () => {
  test.use(as('admin'));

  // SC-09, FR-05, FR-09: M-090。
  test(
    'M-090: ABAC 設定に到達し、属性体系とポリシーの取得が 200 で、seed のポリシーが一覧に揃う',
    { tag: ['@p0', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/admin/authz/attributes', '/bff/admin/authz/policies'];
      const { statuses, bodies } = await openScreen(
        page,
        '/admin/abac',
        '管理者設定（ABAC）',
        apis,
      );
      expect(statuses).toEqual(allOk(apis));
      const body: unknown = await bodies.get('/bff/admin/authz/policies')?.json();
      expect(Array.isArray(body), 'GET /bff/admin/authz/policies の本文は配列').toBe(true);
      const names = (body as { name?: unknown }[]).map((p) => p.name);
      expect(names).toEqual(expect.arrayContaining(ABAC_SEED.policies.map((p) => p.name)));
      await expect(page.getByText(ABAC_SEED.policies[0].name, { exact: true })).not.toHaveCount(0);
    },
  );

  // SC-12, FR-16: M-103。
  test(
    'M-103: MCP クライアント登録管理に到達し、一覧の取得が 200 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/admin/mcp-clients'];
      const { statuses } = await openScreen(
        page,
        '/admin/mcp-clients',
        'MCP クライアント登録管理',
        apis,
      );
      expect(statuses).toEqual(allOk(apis));
    },
  );

  // SC-17, FR-09: M-108。
  test(
    'M-108: ユーザー管理に到達し、一覧の取得が 200 で、試験アカウント 6 名が一覧に居る',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      const apis = ['/bff/admin/users'];
      const { statuses, bodies } = await openScreen(
        page,
        '/admin/users',
        'ユーザーアカウント管理',
        apis,
      );
      expect(statuses).toEqual(allOk(apis));
      // 応答に 6 名が居る（UI の描き方に依らない）。
      const body: unknown = await bodies.get('/bff/admin/users')?.json();
      const usernames = JSON.stringify(body);
      for (const role of ROLES) expect(usernames, `応答の e2e-${role}`).toContain(`"e2e-${role}"`);
      // 画面にも居る。利用者の列は 1 つのセルに表示名とユーザー名を縦に並べる（`UserAccountManagementPage` の user 列）ので、
      // セルの名前の完全一致ではなく、セルの中のユーザー名の行を引く。
      for (const role of ROLES)
        await expect(
          page.getByRole('cell').getByText(`e2e-${role}`, { exact: true }),
        ).toBeVisible();
    },
  );
});

test.describe('e2e-user（ロール無し）の運用の画面', () => {
  test.use(as('user'));

  // SC-11, FR-15, IADR-0009: M-099（陰性側）。構成ビューアは存在秘匿で 404 を返す（403 で存在を漏らさない）。
  test(
    'M-099: ロール無しで構成ビューアを開くと見つかりませんでしたになり、実効構成の取得は 404 になる',
    { tag: ['@p1', '@live-ro'] },
    async ({ page }) => {
      await expectHidden(page, '/admin/config-viewer', '実効構成');
      const res = await page.request.get('/bff/admin/config', { failOnStatusCode: false });
      expect(res.status(), 'GET /bff/admin/config').toBe(404);
    },
  );
});
