// SC-22 / NFR-18 / ADR-0095 / IADR-0453 / IADR-0630 / IADR-0659（#2240 / E-54b）: live-rw の**秘密情報の書き込み**。
// 網羅表（#2013）の M-114 を、`live-setup` が作った `e2e-operator`（書き込める役割）の storageState で流す。
//
// - 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ（層 `rw` の門）。Vault と BFF の書き込みの役は `VAULT=1 VAULT_BFF_WRITER=1`
//   （IADR-0658）が立てる。`E2E_SECRETS_LISTED=1`（Vault が居る）でなければ理由つきで skip する。
// - 🔴 **e2e 専用の項目は足さない**（本番の一覧へ載せない。オーケストレーターの裁定 2026-10-25）。**既存の項目の 1 つ**へ試験用の値を書く。
//   選ぶのは `ast-app-secrets` の `fred-api-key`（株式自動売買の外部の経済指標の API キー）。使い捨てのスタックには ESO が無いので、
//   Vault へ書いた値は Secret へ同期されず、同じ実行の他の試験も、どのアプリケーションの挙動も変えない。
// - 🔴 **値を記録しない**: 値は書く前に発行値として記録し（`support/issuedSecrets.ts`。成果物の走査が探す）、trace・スクリーンショット・
//   動画は切る（入力欄と要求の本文に値が載る）。入力は `fill` ではなく `support/secretInput.ts`（`fill` は値を step の題に載せる）。
//   値に触れる assertion は真偽の「出ていない」だけで（`not.toContain(secret)` は失敗の文言に値を載せる）、失敗の文言にも値を載せない。
// - 🔴 **後始末で空に戻さない**: SC-22 は空の値を受けない（BFF は 400 `invalid-value`。計画 SC-22 / ADR-0095 も空へ戻す操作を定めていない）。
//   書いた値はスタックごと消える。網羅表の「空に戻す」は #2013 で訂正する。
import { randomBytes } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { parseLiveEnv, stateFile } from './support/env';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { recordIssuedSecret } from './support/issuedSecrets';
import { probeBffAudit } from './support/bffAudit';
import { fillSecretInReactInput } from './support/secretInput';
import { ja } from './support/catalog';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.use({
  storageState: stateFile(cfg.authDir, 'operator'),
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

/** 書く先（既存の項目・プロパティ）。 */
const ITEM = 'ast-app-secrets';
const VAULT_PATH = 'ai-stock-trading/app-secrets';
const PROPERTY = 'fred-api-key';
const UPDATE_ACTION = 'secret.item.update';

const T = {
  heading: ja('秘密情報・接続設定の管理'),
  table: ja('秘密情報の項目の一覧'),
  update: ja('更新'),
  property: ja('更新するプロパティ'),
  value: ja('新しい値'),
  confirmation: ja('新しい値（確認のためもう一度）'),
  submit: ja('このプロパティを更新する'),
  write: ja('書き込む'),
  set: ja('設定済み'),
  noRecord: ja('記録なし'),
};

const SKIP_REASON =
  'E2E_SECRETS_LISTED=1 が無い（Vault と BFF の書き込みの役が居る使い捨てのスタックでだけ書く。IADR-0658）';

/** JSON の中に、名前が `key` の鍵が（どの深さにも）在るか。 */
function hasKey(value: unknown, key: string): boolean {
  if (Array.isArray(value)) return value.some((v) => hasKey(v, key));
  if (value !== null && typeof value === 'object')
    return Object.entries(value).some(([k, v]) => k === key || hasKey(v, key));
  return false;
}

/** 画面の操作が送った BFF の要求の応答を待つ（method と pathname で特定する）。 */
function bffResponse(page: Page, method: string, path: string) {
  return page.waitForResponse(
    (r) => r.request().method() === method && new URL(r.url()).pathname === path,
  );
}

/**
 * 「このプロパティを更新する」を押し、確認のダイアログが出たら「書き込む」で確定する（ADR-0110 決定 3）。
 * 確認が出るかは供給元で決まる（「画面以外」は確認なしで送る）。使い捨てのスタックには ESO が無く、供給元は確かめられない。
 */
async function submitWrite(page: Page) {
  const written = bffResponse(page, 'PUT', `/bff/secrets/${ITEM}`);
  await page.getByTestId('secret-update-form').getByRole('button', { name: T.submit }).click();
  const dialog = page.getByRole('alertdialog').or(page.getByRole('dialog'));
  const confirmed = dialog
    .getByRole('button', { name: T.write })
    .click({ timeout: 5_000 })
    .then(
      () => true,
      () => false,
    );
  const res = await written;
  await confirmed;
  return res;
}

const rowOf = (page: Page) =>
  page.getByRole('table', { name: T.table }).getByRole('row').filter({ hasText: VAULT_PATH });

// SC-22, NFR-18, ADR-0095: M-114。
test(
  'M-114: e2e-operator が既存の項目へ試験用の値を書き、値は画面にも一覧の応答にもログにも出ず、書き込みの監査と最終更新者が残る',
  { tag: ['@p1', '@live-rw'] },
  async ({ page }, testInfo) => {
    test.skip(!cfg.secretsListed, SKIP_REASON);
    const secret = `e2e-${cfg.runId ?? 'local'}-${randomBytes(18).toString('hex')}`;
    // 🔴 書く前に記録する（書き込みが途中で落ちても、成果物の走査がこの値を探す）。
    recordIssuedSecret(cfg, 'E2E_ISSUED_SECRET_ITEM_VALUE', secret);

    await page.goto('/admin/secrets');
    await expect(page.getByRole('heading', { name: T.heading, level: 1 })).toBeVisible();
    await rowOf(page).getByRole('button', { name: T.update }).click();
    const form = page.getByTestId('secret-update-form');
    await form.getByLabel(T.property).selectOption(PROPERTY);
    // 🔴 `fill(secret)` は値を step の題に載せ、html レポートに残す。ページの中で入れる。
    await fillSecretInReactInput(form.getByLabel(T.value, { exact: true }), secret);
    await fillSecretInReactInput(form.getByLabel(T.confirmation), secret);

    const res = await submitWrite(page);
    expect(res.status(), `PUT /bff/secrets/${ITEM}`).toBe(200);
    const body = (await res.json()) as { item?: string; property?: string; version?: unknown };
    expect(hasKey(body, 'value'), '書き込みの応答の value').toBe(false);
    expect(body.item).toBe(ITEM);
    expect(body.property).toBe(PROPERTY);
    expect(Number.isInteger(body.version), '書き込みの応答の版').toBe(true);
    const version = body.version as number;
    await expect(page.getByTestId('secret-update-done')).toBeVisible();
    // 🔴 送った値は入力欄にも画面にも残らない（成功したら入力を空に戻す）。
    expect((await page.content()).includes(secret), '書いた値が画面の DOM に残った').toBe(false);

    // 再読込: 一覧の応答に値の鍵も値も無く、行は「設定済み」で最終更新者が記録されている。
    const listed = bffResponse(page, 'GET', '/bff/secrets');
    await page.reload();
    const list = await listed;
    expect(list.status(), 'GET /bff/secrets').toBe(200);
    const listText = await list.text();
    expect(hasKey(JSON.parse(listText), 'value'), '一覧の応答の value').toBe(false);
    expect(listText.includes(secret), '書いた値が一覧の応答に出た').toBe(false);
    await expect(rowOf(page).getByText(T.set)).toBeVisible();
    await expect(rowOf(page).getByText(T.noRecord)).toHaveCount(0);
    expect((await page.content()).includes(secret), '書いた値が再読込の後の画面に出た').toBe(false);

    // 書き込みの監査（BFF の構造化ログ）: 項目・プロパティ・版が granted で 1 行残り、書いた値はログのどこにも出ない。
    const audit = probeBffAudit(cfg, UPDATE_ACTION, secret);
    expect(audit.leaked, '書いた値が BFF のログに出た').toBe(false);
    const granted = audit.lines.filter(
      (l) =>
        l.includes(' outcome=granted ') &&
        l.includes(`item=${ITEM} property=${PROPERTY} version=${version}`),
    );
    expect(granted, `監査 ${UPDATE_ACTION} granted（版 ${version}）の行`).toHaveLength(1);
    // 最終更新者の表示は、監査の主体（利用者名）と同じである。
    const subject = /subject=(\S+)/.exec(granted[0] ?? '')?.[1] ?? '';
    expect(subject, '監査の主体').not.toBe('');
    await expect(rowOf(page).getByText(subject, { exact: true })).toBeVisible();
    testInfo.annotations.push({
      type: 'SC-22 の書き込み',
      description: `${ITEM}/${PROPERTY} 版 ${version}`,
    });
  },
);
