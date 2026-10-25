// NFR / FR-05 / FR-19 / UC-11 / SC-03 / SC-19 / IADR-0629 / IADR-0630（#2031 / E-51）: live-rw の**個人資料の一生と
// 共有の動的束縛**。網羅表（#2013）の M-062・M-063 を、`live-setup` が作った `e2e-user`（作る側）と
// `e2e-operator`（共有される側）の storageState で流す。
//
// - 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ。層 `rw` は globalSetup の門（`support/readiness.ts` の
//   assertDisposableStack）が kubectl の context を確かめてから流れる。
// - 作るものの名前は `e2e-<runId>-…`（`support/cleanup.ts` の e2eName）。作った直後に後始末を積み（`track`）、afterEach で消す。
//   本文が消し終えていれば後始末は 404（消えた扱い）。残りは teardown の sweep（dry-run）が数える。
// - 🔴 共有の判定は SC-03（`/docs/<id>`）で見る。共有の前と取消の後は BFF が 404 で「文書が見つかりませんでした」、共有中だけ見える
//   （seed の「共有された個人資料を読める」＝ `shared_with` に `${current_user}`。機密区分の既定 restricted とは独立）。
//   SC-02（検索）では見ない —— 作った資料は本文を持たず、露出「横断検索に含める」は既定 OFF で索引に入らない（IADR-0630 決定 7）。
// - M-067（SC-20 の端末）は `devices.rw.spec.ts`（trace・スクリーンショット・動画を切るのはファイルの先頭でしかできないので分けた）。
import { expect, test, type Browser, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { createCleanup, e2eName } from './support/cleanup';
import { ja } from './support/catalog';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

const T = {
  notesHeading: ja('個人資料'),
  title: ja('タイトル'),
  create: ja('作成する'),
  created: ja('個人資料を作成しました。'),
  share: ja('共有先を変更する'),
  shareDialog: (noteTitle: string) => ja('「{noteTitle}」の共有先', { noteTitle }),
  person: ja('個人'),
  searchByName: ja('名前で検索する'),
  candidates: ja('利用者の候補'),
  add: ja('追加'),
  added: ja('指定先を追加しました。'),
  revoke: ja('取り消す'),
  revoked: ja('指定先を取り消しました。'),
  close: ja('閉じる'),
  sharedOne: ja('個人指定（{userCount} 人）', { userCount: '1' }),
  privateBadge: ja('非公開'),
  softDelete: ja('削除する'),
  softDeleteDialog: ja('個人資料を削除しますか？'),
  softDeleted: ja('個人資料を削除しました。'),
  purge: ja('完全に削除する'),
  purgeDialog: ja('選択した個人資料を完全に削除しますか？'),
  purged: ja('個人資料を完全に削除しました。'),
  docNotFound: ja('文書が見つかりませんでした。'),
};

/** 共有の付与・取消が読み取りへ反映されるのを待つ上限（時間で待たず、開き直しながら待つ）。 */
const BIND_WAIT = { timeout: 30_000, intervals: [1_000, 2_000, 5_000] };

const isBff = (url: string, path: string) => new URL(url).pathname === path;

/** 画面の操作が送った BFF の要求の応答を待つ（method と pathname で特定する）。 */
function bffResponse(page: Page, method: string, path: string | RegExp) {
  return page.waitForResponse((r) => {
    if (r.request().method() !== method) return false;
    const p = new URL(r.url()).pathname;
    return typeof path === 'string' ? p === path : path.test(p);
  });
}

/** e2e-operator の別の context（共有される側）。 */
async function operatorPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({
    storageState: stateFile(cfg.authDir, 'operator'),
    baseURL: cfg.edgeUrl,
    ignoreHTTPSErrors: cfg.insecureTls,
    locale: 'ja-JP',
  });
  return context.newPage();
}

/** e2e-operator が SC-03 で資料を開いたときの、BFF の応答の状態。画面の表示（見出し／見つからない）も同じ状態に合わせて確かめる。 */
async function expectOperatorSees(page: Page, id: string, title: string, visible: boolean) {
  await expect(async () => {
    const responded = page.waitForResponse(
      (r) => isBff(r.url(), `/bff/documents/${id}`) && r.request().method() === 'GET',
    );
    await page.goto(`/docs/${id}`);
    expect((await responded).status(), `GET /bff/documents/{id}（e2e-operator）`).toBe(
      visible ? 200 : 404,
    );
    if (visible) {
      await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible();
    } else {
      await expect(page.getByText(T.docNotFound, { exact: true })).toBeVisible();
      await expect(page.getByRole('heading', { name: title })).toHaveCount(0);
    }
  }).toPass(BIND_WAIT);
}

test.describe('e2e-user の個人資料（書き込み）', () => {
  test.use({ storageState: stateFile(cfg.authDir, 'user') });

  let cleanup: ReturnType<typeof createCleanup>;

  test.beforeEach(({ page, storageState }) => {
    cleanup = createCleanup(page.request, storageState);
  });

  test.afterEach(async ({}, testInfo) => {
    await cleanup.run(testInfo);
  });

  // SC-19, SC-03, UC-11, FR-19, FR-05: M-062（一生）・M-063（共有の動的束縛）。状態が続くので 1 つの test で段を踏む。
  test(
    'M-062 / M-063: 作成 → e2e-operator へ個人指定で共有 → 取消 → 論理削除 → 完全削除が通り、共有中だけ e2e-operator の SC-03 で見える',
    { tag: ['@p0', '@live-rw'] },
    async ({ page, browser }, testInfo) => {
      const title = e2eName(cfg.runId, 'note', testInfo.retry);
      const operator = await operatorPage(browser);
      try {
        let id = '';

        await test.step('作成（SC-19）', async () => {
          await page.goto('/my/notes');
          await expect(page.getByRole('heading', { name: T.notesHeading, level: 1 })).toBeVisible();
          await page.getByLabel(T.title).fill(title);
          const created = bffResponse(page, 'POST', '/bff/private-notes');
          await page.getByRole('button', { name: T.create, exact: true }).click();
          const res = await created;
          expect(res.status(), 'POST /bff/private-notes').toBe(201);
          id = ((await res.json()) as { id: string }).id;
          // 作った直後に積む（以降の段で落ちても afterEach が消す）。論理削除 → 完全削除。
          cleanup.track(
            title,
            { method: 'DELETE', path: `/bff/private-notes/${id}` },
            { method: 'POST', path: '/bff/private-notes/purge', data: { ids: [id] } },
          );
          await expect(page.getByText(T.created, { exact: true })).toBeVisible();
          await expect(page.getByRole('row').filter({ hasText: title })).toContainText(
            T.privateBadge,
          );
        });

        const row = page.getByRole('row').filter({ hasText: title });
        const dialog = page.getByRole('dialog', { name: T.shareDialog(title) });

        await test.step('共有の前は e2e-operator に見えない（陰性対照）', async () => {
          await expectOperatorSees(operator, id, title, false);
        });

        await test.step('e2e-operator へ個人指定で共有する', async () => {
          await row.getByRole('button', { name: T.share, exact: true }).click();
          await expect(dialog).toBeVisible();
          await dialog.getByRole('radio', { name: T.person, exact: true }).check();
          await dialog.getByLabel(T.searchByName).fill('Operator');
          const option = dialog
            .getByRole('listbox', { name: T.candidates })
            .getByRole('option')
            .filter({ hasText: 'E2E' })
            .filter({ hasText: 'Operator' });
          await expect(option).toHaveCount(1);
          await option.click();
          const granted = bffResponse(page, 'POST', `/bff/private-notes/${id}/shares`);
          await dialog.getByRole('button', { name: T.add, exact: true }).click();
          expect((await granted).status(), 'POST /bff/private-notes/{id}/shares').toBe(201);
          await expect(dialog.getByText(T.added, { exact: true })).toBeVisible();
          await dialog.getByRole('button', { name: T.close, exact: true }).click();
          await expect(row).toContainText(T.sharedOne);
        });

        await test.step('共有中は e2e-operator の SC-03 で見える', async () => {
          await expectOperatorSees(operator, id, title, true);
        });

        await test.step('共有を取り消す', async () => {
          await row.getByRole('button', { name: T.share, exact: true }).click();
          await expect(dialog).toBeVisible();
          const revoked = bffResponse(
            page,
            'DELETE',
            new RegExp(`^/bff/private-notes/${id}/shares/user/`),
          );
          await dialog.getByRole('button', { name: T.revoke, exact: true }).click();
          expect((await revoked).status(), 'DELETE /bff/private-notes/{id}/shares/…').toBe(204);
          await expect(dialog.getByText(T.revoked, { exact: true })).toBeVisible();
          await dialog.getByRole('button', { name: T.close, exact: true }).click();
          await expect(row).toContainText(T.privateBadge);
        });

        await test.step('取消の後は e2e-operator に見えない', async () => {
          await expectOperatorSees(operator, id, title, false);
        });

        await test.step('論理削除（ゴミ箱へ）', async () => {
          await row.getByRole('button', { name: T.softDelete, exact: true }).click();
          const confirm = page.getByRole('dialog', { name: T.softDeleteDialog });
          const deleted = bffResponse(page, 'DELETE', `/bff/private-notes/${id}`);
          await confirm.getByRole('button', { name: T.softDelete, exact: true }).click();
          expect((await deleted).ok(), 'DELETE /bff/private-notes/{id}').toBe(true);
          await expect(page.getByText(T.softDeleted, { exact: true })).toBeVisible();
          await expect(row).toHaveCount(0);
        });

        await test.step('完全削除（削除済みタブ）', async () => {
          await page.goto('/my/notes?tab=trash');
          const trashed = page.getByRole('row').filter({ hasText: title });
          await expect(trashed).toHaveCount(1);
          await trashed.getByRole('button', { name: T.purge, exact: true }).click();
          const confirm = page.getByRole('dialog', { name: T.purgeDialog });
          const purged = bffResponse(page, 'POST', '/bff/private-notes/purge');
          await confirm.getByRole('button', { name: T.purge, exact: true }).click();
          expect((await purged).status(), 'POST /bff/private-notes/purge').toBe(200);
          await expect(page.getByText(T.purged, { exact: true })).toBeVisible();
          await expect(trashed).toHaveCount(0);
        });
      } finally {
        await operator.context().close();
      }
    },
  );
});
