// NFR / FR-05 / FR-06 / UC-03 / UC-05 / SC-02 / SC-05 / SC-06 / SC-09 / IADR-0629 / IADR-0630 / IADR-0631（#2032 / E-52）: live-rw の
// **管理リソースの作成と後始末**。網羅表（#2013）の M-076（文書）・M-082（データソース）・M-092（ABAC ポリシー）を、`live-setup` が作った
// `e2e-admin` の storageState で流す。M-104（MCP クライアント）は secret を扱うので `mcp-clients.rw.spec.ts`（trace 等を切る）に分けた。
//
// - 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ（層 `rw` の門。`support/readiness.ts` の assertDisposableStack）。
// - 名前は `e2e-<runId>-<種類>-r<再試行>`（`support/cleanup.ts` の e2eName）。作った直後に後始末を積み、afterEach で消す。
//   データソースは DELETE が実体を消さず無効化する（行は残る）。sweep は無効化済みを後始末済みとして数えない（IADR-0631 決定 1）。
// - 🔴 **既存のポリシーと辞書に差分が無い**: 各 test の前後で、ポリシー・属性辞書・タグ辞書・関係の型の辞書を読み（`support/dictionaries.ts`）、
//   `e2e-` の行と件数の項目を除いて一致することを見る（IADR-0631 決定 4）。
// - 🔴 作るポリシーは**何も新しく許さない**: 利用者 clearance=restricted × 文書 confidentiality=restricted の read（seed の「restricted 取扱者は
//   全区分を読める」に含まれる）。作った間も同じ実行の他の試験の認可が変わらない（IADR-0631 決定 2）。
// - M-076 の SC-02: 登録フォームは本文を持たないが、本文の無い文書も索引のメタデータ点（タイトル）を持つので、タイトルの runId で検索に出る。
//   `E2E_SEARCH_HITS=1` のときだけ判定する（IADR-0627・IADR-0631 決定 3）。
import { expect, test, type Page } from '@playwright/test';
import type { SearchResponse } from '../../src/lib/api/generated/bff.schemas';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { createCleanup, e2eName } from './support/cleanup';
import { dictionarySnapshot } from './support/dictionaries';
import { ja } from './support/catalog';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.use({ storageState: stateFile(cfg.authDir, 'admin') });

const T = {
  docsHeading: ja('文書一覧'),
  newDoc: ja('＋ 新規登録'),
  docForm: ja('文書登録'),
  title: ja('タイトル'),
  confidentiality: ja('機密区分（ABAC属性）'),
  save: ja('保存'),
  docCreated: ja('文書を登録しました。'),
  publish: ja('公開'),
  published: ja('文書を公開しました。'),
  archive: ja('アーカイブ'),
  archived: ja('文書をアーカイブしました。'),
  remove: ja('削除'),
  docDeleted: ja('文書を削除しました。'),
  searchHeading: ja('検索結果一覧'),
  sourcesHeading: ja('データソース'),
  newSource: ja('＋ ソース登録'),
  sourceForm: ja('データソース登録'),
  sourceName: ja('名前'),
  sourceType: ja('種別'),
  sourceUri: ja('接続先 URI'),
  register: ja('登録する'),
  sourceCreated: ja('データソースを登録しました。'),
  disable: ja('無効化'),
  sourceDisabled: ja('データソースを無効化しました。'),
  disabledBadge: ja('無効'),
  abacHeading: ja('管理者設定（ABAC）'),
  policyForm: ja('ポリシー登録'),
  policyName: ja('名前（必須）'),
  policyAction: ja('対象アクション'),
  policyAttr: ja('対象属性'),
  policyValue: ja('条件の値'),
  addCondition: ja('条件を追加'),
  validate: ja('検証'),
  noConflict: ja('矛盾なし'),
  conflict: ja('矛盾あり'),
  policySaved: ja('ポリシーを保存しました。認可判定へ即時反映されます。'),
  deletePolicy: (policyName: string) => ja('ポリシーを削除: {policyName}', { policyName }),
};

const HITS_REASON =
  'E2E_SEARCH_HITS=1 が無い（索引が空であり得る環境では、検索に出ることを判定しない。IADR-0627）';
/** 取り込みを待つ上限（公開は非同期に索引へ写る）。 */
const INDEX_WAIT = { timeout: 120_000, intervals: [3_000, 5_000, 10_000] };

/** 画面の操作が送った BFF の要求の応答を待つ（method と pathname で特定する）。 */
function bffResponse(page: Page, method: string, path: string | RegExp) {
  return page.waitForResponse((r) => {
    if (r.request().method() !== method) return false;
    const p = new URL(r.url()).pathname;
    return typeof path === 'string' ? p === path : path.test(p);
  });
}

/** `<select>` の中から表示が `prefix` で始まる選択肢の表示（ラベル）を引く（属性の ID は実行ごとに違うので、ラベルで選ぶ）。 */
async function optionLabelStartingWith(page: Page, selectLabel: string, prefix: string) {
  const texts = (await page.getByLabel(selectLabel).locator('option').allTextContents()).map((t) =>
    t.trim(),
  );
  const found = texts.find((t) => t.startsWith(prefix));
  expect(
    found,
    `「${selectLabel}」に「${prefix}」で始まる選択肢が無い（${texts.join(' / ')}）`,
  ).toBeDefined();
  return found!;
}

let cleanup: ReturnType<typeof createCleanup>;
let before: Awaited<ReturnType<typeof dictionarySnapshot>>;

test.beforeEach(async ({ page, storageState }) => {
  cleanup = createCleanup(page.request, storageState);
  before = await dictionarySnapshot(page.request);
});

test.afterEach(async ({ page }, testInfo) => {
  await cleanup.run(testInfo);
  // 既存のポリシー・辞書に差分が無い（後始末の後に比べる。e2e- の行と件数の項目は除く）。
  expect(await dictionarySnapshot(page.request), '既存のポリシー・辞書が変わった').toEqual(before);
});

// SC-05, SC-02, UC-03, FR-06: M-076。
test(
  'M-076: e2e-admin が文書を登録 → 公開 → SC-02 の検索で見える → アーカイブ → 削除できる',
  { tag: ['@p0', '@live-rw'] },
  async ({ page }, testInfo) => {
    const title = e2eName(cfg.runId, 'doc', testInfo.retry);
    await page.goto('/admin/documents');
    await expect(page.getByRole('heading', { name: T.docsHeading, level: 1 })).toBeVisible();

    let id = '';

    await test.step('登録（タグは付けない。機密区分は社内限）', async () => {
      await page.getByRole('button', { name: T.newDoc, exact: true }).click();
      const form = page.getByRole('form', { name: T.docForm });
      await form.getByLabel(T.title).fill(title);
      await form.getByLabel(T.confidentiality).selectOption('internal');
      const created = bffResponse(page, 'POST', '/bff/documents');
      await form.getByRole('button', { name: T.save, exact: true }).click();
      const res = await created;
      expect(res.status(), 'POST /bff/documents').toBe(201);
      id = ((await res.json()) as { id: string }).id;
      cleanup.track(title, { method: 'DELETE', path: `/bff/documents/${id}` });
      await expect(page.getByText(T.docCreated, { exact: true })).toBeVisible();
    });

    const row = page.getByRole('row').filter({ hasText: title });

    await test.step('公開', async () => {
      const published = bffResponse(page, 'POST', `/bff/documents/${id}/publish`);
      await row.getByRole('button', { name: T.publish, exact: true }).click();
      expect((await published).status(), 'POST /bff/documents/{id}/publish').toBe(200);
      await expect(page.getByText(T.published, { exact: true })).toBeVisible();
      await expect(row.getByRole('button', { name: T.publish, exact: true })).toHaveCount(0);
    });

    await test.step('SC-02 の検索で見える（タイトルの runId で引く）', async () => {
      if (!cfg.searchHits) {
        testInfo.annotations.push({ type: 'SC-02 を判定していない', description: HITS_REASON });
        return;
      }
      const search = await page.context().newPage();
      try {
        await expect(async () => {
          const responded = bffResponse(search, 'POST', '/bff/search');
          await search.goto(`/search?q=${encodeURIComponent(cfg.runId ?? '')}`);
          const res = await responded;
          expect(res.status(), 'POST /bff/search').toBe(200);
          const body = (await res.json()) as SearchResponse;
          expect(body.results.map((r) => r.documentId)).toContain(id);
        }).toPass(INDEX_WAIT);
        await expect(
          search.getByRole('heading', { name: T.searchHeading, level: 1 }),
        ).toBeVisible();
        await expect(search.getByRole('link', { name: title })).toBeVisible();
      } finally {
        await search.close();
      }
    });

    await test.step('アーカイブ', async () => {
      const archived = bffResponse(page, 'POST', `/bff/documents/${id}/archive`);
      await row.getByRole('button', { name: T.archive, exact: true }).click();
      expect((await archived).status(), 'POST /bff/documents/{id}/archive').toBe(200);
      await expect(page.getByText(T.archived, { exact: true })).toBeVisible();
    });

    await test.step('削除', async () => {
      const deleted = bffResponse(page, 'DELETE', `/bff/documents/${id}`);
      await row.getByRole('button', { name: T.remove, exact: true }).click();
      expect((await deleted).status(), 'DELETE /bff/documents/{id}').toBe(204);
      await expect(page.getByText(T.docDeleted, { exact: true })).toBeVisible();
      await expect(row).toHaveCount(0);
    });
  },
);

// SC-06, FR-05: M-082。手動同期は押さない（接続先は到達しないダミー）。
test(
  'M-082: e2e-admin がデータソース（接続先は到達しないダミー）を登録し、無効化できる',
  { tag: ['@p2', '@live-rw'] },
  async ({ page }, testInfo) => {
    const name = e2eName(cfg.runId, 'source', testInfo.retry);
    await page.goto('/admin/sources');
    await expect(page.getByRole('heading', { name: T.sourcesHeading, level: 1 })).toBeVisible();
    await page.getByRole('button', { name: T.newSource, exact: true }).click();
    const form = page.getByRole('form', { name: T.sourceForm });
    await form.getByLabel(T.sourceName).fill(name);
    await form.getByLabel(T.sourceType).selectOption('filesystem');
    await form.getByLabel(T.sourceUri).fill(`file:///nonexistent/${name}`);
    const created = bffResponse(page, 'POST', '/bff/datasources');
    await form.getByRole('button', { name: T.register, exact: true }).click();
    const res = await created;
    expect(res.status(), 'POST /bff/datasources').toBe(201);
    const id = ((await res.json()) as { id: string }).id;
    // DELETE は無効化（行は残る。sweep は無効化済みを後始末済みとして数えない）。
    cleanup.track(name, { method: 'DELETE', path: `/bff/datasources/${id}` });
    await expect(page.getByText(T.sourceCreated, { exact: true })).toBeVisible();

    const row = page.getByRole('row').filter({ hasText: name });
    const disabled = bffResponse(page, 'DELETE', `/bff/datasources/${id}`);
    await row.getByRole('button', { name: T.disable, exact: true }).click();
    expect((await disabled).status(), 'DELETE /bff/datasources/{id}').toBe(204);
    await expect(page.getByText(T.sourceDisabled, { exact: true })).toBeVisible();
    await expect(row.getByText(T.disabledBadge, { exact: true })).toBeVisible();
    await expect(row.getByRole('button', { name: T.disable, exact: true })).toHaveCount(0);
  },
);

// SC-09, UC-05, FR-05: M-092。
test(
  'M-092: e2e-admin がポリシーを検証（保存されない）→ 保存 → 削除できる。何も新しく許さない条件で作る',
  { tag: ['@p1', '@live-rw'] },
  async ({ page }, testInfo) => {
    const name = e2eName(cfg.runId, 'policy', testInfo.retry);
    await page.goto('/admin/abac');
    await expect(page.getByRole('heading', { name: T.abacHeading, level: 1 })).toBeVisible();
    const form = page.getByRole('form', { name: T.policyForm });
    await form.getByLabel(T.policyName).fill(name);
    await form.getByLabel(T.policyAction).selectOption('read');
    // 利用者 clearance=restricted × 文書 confidentiality=restricted（seed の「restricted 取扱者は全区分を読める」に含まれる）。
    for (const prefix of ['取扱可能区分（', '機密区分（']) {
      await form
        .getByLabel(T.policyAttr)
        .selectOption({ label: await optionLabelStartingWith(page, T.policyAttr, prefix) });
      await form.getByLabel(T.policyValue).selectOption('restricted');
      await form.getByRole('button', { name: T.addCondition, exact: true }).click();
    }

    await test.step('検証（dry-run）は保存しない', async () => {
      const validated = bffResponse(page, 'POST', '/bff/admin/authz/policies/validate');
      await form.getByRole('button', { name: T.validate, exact: true }).click();
      expect((await validated).status(), 'POST /bff/admin/authz/policies/validate').toBe(200);
      await expect(
        page
          .getByText(T.noConflict, { exact: true })
          .or(page.getByText(T.conflict, { exact: true })),
      ).toBeVisible();
      const listed = await page.request.get('/bff/admin/authz/policies');
      expect(((await listed.json()) as { name: string }[]).map((p) => p.name)).not.toContain(name);
    });

    let id = '';

    await test.step('保存', async () => {
      const saved = bffResponse(page, 'POST', '/bff/admin/authz/policies');
      await form.getByRole('button', { name: T.save, exact: true }).click();
      const res = await saved;
      expect(res.status(), 'POST /bff/admin/authz/policies').toBe(201);
      id = ((await res.json()) as { id: string }).id;
      cleanup.track(name, { method: 'DELETE', path: `/bff/admin/authz/policies/${id}` });
      await expect(page.getByText(T.policySaved, { exact: true })).toBeVisible();
      await expect(page.getByRole('row').filter({ hasText: name })).toHaveCount(1);
    });

    await test.step('削除', async () => {
      const deleted = bffResponse(page, 'DELETE', `/bff/admin/authz/policies/${id}`);
      await page.getByRole('button', { name: T.deletePolicy(name), exact: true }).click();
      expect((await deleted).status(), 'DELETE /bff/admin/authz/policies/{id}').toBe(204);
      await expect(page.getByRole('row').filter({ hasText: name })).toHaveCount(0);
    });
  },
);
