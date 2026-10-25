// NFR / FR-20 / SC-20 / IADR-0629 / IADR-0630（#2031 / E-51）: live-rw の**同期端末の登録・トークン発行・個別失効**。網羅表（#2013）の
// M-067 を、`live-setup` が作った `e2e-user` の storageState で流す。
//
// - 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ（層 `rw` の門。`support/readiness.ts` の assertDisposableStack）。
// - 🔴 **トークンを記録しない**（IADR-0630 決定 6）。平文のトークンは発行の応答と画面に 1 回だけ出る。trace は応答の本文と DOM を、
//   スクリーンショットと動画は画面を記録するので、このファイルでは 3 つとも切る（Playwright はファイルの先頭か config でしか
//   切り替えられないので、M-062 / M-063 の `private-notes.rw.spec.ts` と分けた）。発行の応答からは deviceId だけを取り、応答の直後に
//   画面を開き直してトークンの表示を消す（以降の失敗の error-context にも載らない）。トークンに触れる assertion を置かない。
//   トークンは発行の直後に `support/issuedSecrets.ts` で記録し、成果物の走査（`check-e2e-artifact-secrets.js --issued-file`）が探す。
// - 端末の名前は `e2e-<runId>-device-r<再試行>`。作った直後に後始末（失効）を積む。端末の行は失効の後も履歴として残る（網羅表のとおり。
//   BFF に端末の行を消す口は無い）。
import { expect, test, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { createCleanup, e2eName } from './support/cleanup';
import { ja } from './support/catalog';
import { recordIssuedSecret } from './support/issuedSecrets';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.use({
  storageState: stateFile(cfg.authDir, 'user'),
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

const T = {
  obsidianHeading: ja('Obsidian 連携設定'),
  deviceName: ja('端末名（任意）'),
  issue: ja('トークンを発行する'),
  revokeDevice: ja('この端末を失効する'),
  revokeDeviceDialog: ja('この端末を失効しますか？'),
  revokeDeviceConfirm: ja('失効する'),
  deviceRevoked: ja('失効済み'),
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

// SC-20, FR-20: M-067。
test(
  'M-067: 端末を登録してトークンを発行し（記録しない）、その端末を個別に失効すると「失効済み」になる',
  { tag: ['@p1', '@live-rw'] },
  async ({ page }, testInfo) => {
    const name = e2eName(cfg.runId, 'device', testInfo.retry);
    await page.goto('/my/obsidian');
    await expect(page.getByRole('heading', { name: T.obsidianHeading, level: 1 })).toBeVisible();
    await page.getByLabel(T.deviceName).fill(name);
    const issued = bffResponse(page, 'POST', '/bff/private-notes/devices');
    await page.getByRole('button', { name: T.issue, exact: true }).click();
    const res = await issued;
    expect(res.status(), 'POST /bff/private-notes/devices').toBe(201);
    const body = (await res.json()) as { deviceId: string; token: string };
    // 🔴 トークンは成果物の走査が探せるように記録するだけで、出さない・比べない（IADR-0630 決定 6。失効の後も同じに扱う）。
    recordIssuedSecret(cfg, 'E2E_ISSUED_DEVICE_TOKEN', body.token);
    const deviceId = body.deviceId;
    cleanup.track(name, { method: 'DELETE', path: `/bff/private-notes/devices/${deviceId}` });
    // 画面を開き直してトークンの表示を消す（以降の失敗の記録に平文が載らない）。
    await page.reload();
    await expect(page.getByRole('heading', { name: T.obsidianHeading, level: 1 })).toBeVisible();

    const row = page.getByRole('row').filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await row.getByRole('button', { name: T.revokeDevice, exact: true }).click();
    const confirm = page.getByRole('dialog', { name: T.revokeDeviceDialog });
    const revoked = bffResponse(page, 'DELETE', `/bff/private-notes/devices/${deviceId}`);
    await confirm.getByRole('button', { name: T.revokeDeviceConfirm, exact: true }).click();
    expect((await revoked).status(), 'DELETE /bff/private-notes/devices/{id}').toBe(204);
    await expect(row).toContainText(T.deviceRevoked);
    await expect(row.getByRole('button', { name: T.revokeDevice })).toHaveCount(0);
  },
);
