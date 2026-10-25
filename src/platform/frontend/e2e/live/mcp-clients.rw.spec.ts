// NFR / FR-16 / UC-09 / SC-12 / IADR-0630 / IADR-0631（#2032 / E-52）: live-rw の**無人の MCP クライアントの登録と無効化**。
// 網羅表（#2013）の M-104 を、`live-setup` が作った `e2e-admin` の storageState で流す。
//
// - 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ（層 `rw` の門）。
// - 🔴 **client secret を記録しない**（IADR-0630 決定 6・IADR-0631 決定 5）。平文の secret は登録の応答と画面に 1 回だけ出る。trace は応答の
//   本文と DOM を、スクリーンショットと動画は画面を記録するので、このファイルでは 3 つとも切る（ファイルの先頭でしか切れないので
//   `admin-resources.rw.spec.ts` と分けた）。応答からは clientId を取り、**後始末（無効化）を積んでから** secret を記録し
//   （`support/issuedSecrets.ts`。成果物の走査が探す）、画面の「閉じる」で表示を消す。secret に触れる assertion を置かない。
// - 🔴 **BFF に MCP クライアントの削除の口は無い**（無効化だけ）。網羅表の「削除」は流せないので、無効化までを流す。無効化済みの行は残り、
//   sweep は後始末済みとして数えない（IADR-0631 決定 1）。属性は付けない（何も読めない無人アカウント。登録者の部分集合の判定も呼ばれない）。
import { expect, test, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { createCleanup, e2eName } from './support/cleanup';
import { recordIssuedSecret } from './support/issuedSecrets';
import { ja } from './support/catalog';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.use({
  storageState: stateFile(cfg.authDir, 'admin'),
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

const T = {
  heading: ja('MCP クライアント登録管理'),
  kind: ja('クライアント種別'),
  clientId: ja('クライアント ID'),
  displayName: ja('表示名'),
  register: ja('登録'),
  close: ja('閉じる'),
  disable: ja('無効化'),
  disabled: ja('無効（即時接続拒否）'),
};

/** 画面の操作が送った BFF の要求の応答を待つ（method と pathname で特定する）。 */
function bffResponse(page: Page, method: string, path: string) {
  return page.waitForResponse(
    (r) => r.request().method() === method && new URL(r.url()).pathname === path,
  );
}

let cleanup: ReturnType<typeof createCleanup>;

test.beforeEach(({ page, storageState }) => {
  cleanup = createCleanup(page.request, storageState);
});

test.afterEach(async ({}, testInfo) => {
  await cleanup.run(testInfo);
});

// SC-12, FR-16, UC-09: M-104。
test(
  'M-104: e2e-admin が無人の MCP クライアントを登録し（secret は記録しない）、無効化すると「無効（即時接続拒否）」になる',
  { tag: ['@p2', '@live-rw'] },
  async ({ page }, testInfo) => {
    const name = e2eName(cfg.runId, 'mcp', testInfo.retry);
    await page.goto('/admin/mcp-clients');
    await expect(page.getByRole('heading', { name: T.heading, level: 1 })).toBeVisible();
    await page.getByLabel(T.kind).selectOption('service-account');
    await page.getByLabel(T.clientId).fill(name);
    await page.getByLabel(T.displayName).fill(name);
    const registered = bffResponse(page, 'POST', '/bff/admin/mcp-clients');
    await page.getByRole('button', { name: T.register, exact: true }).click();
    const res = await registered;
    expect(res.status(), 'POST /bff/admin/mcp-clients').toBe(201);
    const body = (await res.json()) as { clientId: string; clientSecret?: string | null };
    // 後始末（無効化。削除の口は無い）を先に積む（記録が例外を投げても、登録したクライアントは afterEach が無効にする）。
    cleanup.track(name, {
      method: 'POST',
      path: `/bff/admin/mcp-clients/${encodeURIComponent(body.clientId)}/disable`,
    });
    // 🔴 secret は成果物の走査が探せるように記録するだけで、出さない・比べない（無効化の後も同じに扱う）。
    expect(typeof body.clientSecret, '無人の登録の応答に client secret が無い').toBe('string');
    recordIssuedSecret(cfg, 'E2E_ISSUED_MCP_CLIENT_SECRET', body.clientSecret as string);
    // 表示を消す（以降の失敗の記録に平文が載らない）。
    await page
      .getByTestId('issued-secret')
      .getByRole('button', { name: T.close, exact: true })
      .click();
    await expect(page.getByTestId('issued-secret')).toHaveCount(0);

    const row = page.getByRole('row').filter({ hasText: name });
    await expect(row).toHaveCount(1);
    const disabled = bffResponse(
      page,
      'POST',
      `/bff/admin/mcp-clients/${encodeURIComponent(body.clientId)}/disable`,
    );
    await row.getByRole('button', { name: T.disable, exact: true }).click();
    expect((await disabled).status(), 'POST /bff/admin/mcp-clients/{clientId}/disable').toBe(200);
    await expect(row.getByText(T.disabled, { exact: true })).toBeVisible();
  },
);
