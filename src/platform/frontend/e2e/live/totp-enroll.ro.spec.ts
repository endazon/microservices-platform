// NFR-10 / SC-14 / ADR-0026 / IADR-0662（#2040 / E-81）: live の**初回の TOTP 登録**（網羅表〔#2013〕の M-005）。
// TOTP を登録していない一時アカウントで、パスワードの後に登録の画面（必須アクション CONFIGURE_TOTP）へ誘導され、
// QR コード・手動入力キー・デバイス名・確認コードで登録を終えて元の画面へ戻ること、次のログインでは登録の画面を出さず
// 登録した TOTP だけを求めることを見る。
//
// - 🔴 **使い捨てのスタックの中だけ**（`E2E_DISPOSABLE=1`。IADR-0662 決定 1）。一時アカウントを Keycloak の管理 API で作って消すので、
//   常設の PoC では project から外れて 0 件になる（`support/projects.ts`）。向け先の門は globalSetup（`readiness.ts`）が live-rw と同じ判定器で確かめる。
// - 一時アカウントは `e2e-<runId>-totp-enroll-r<再試行>`。作成は realm の既定（`CONFIGURE_TOTP` が defaultAction）に任せ、
//   requiredActions を書き足さない —— **新しい利用者が実際に受ける誘導**を試す（作った直後に `CONFIGURE_TOTP` だけが付いていることを見る）。
//   後始末は afterAll の削除（スタックごと消えるが、同じ実行の他の試験に残さない）。
// - 🔴 秘密（パスワード・TOTP の秘密・確認コード）を扱うので trace・スクリーンショット・動画を切る。パスワードと TOTP の秘密は
//   作った・読んだ直後に発行値として記録する（成果物の走査が探す）。ログ・attach・例外の文言へ出さない。
// - 🔴 **リカバリーコードの 1 回表示（網羅表 M-005 の後半・計画 SC-14）はここで見ない**（IADR-0662 決定 4）。realm の宣言では
//   `CONFIGURE_TOTP` の設定 `add-recovery-codes` が既定の false で、browser フローにリカバリーコードの段も無いので、登録の後に
//   リカバリーコードの画面は出ない（Keycloak 26.7.4 の UpdateTotp）。出るようになったら、登録の後の待ちが必須アクションの画面で止まり、
//   この spec が赤で知らせる（そのとき後半を足す）。
import { randomBytes } from 'node:crypto';
import { expect, test, type Page } from '@playwright/test';
import { e2eName } from './support/cleanup';
import { parseLiveEnv, REALM } from './support/env';
import { recordIssuedSecret } from './support/issuedSecrets';
import { openKeycloakAdmin, type KeycloakAdmin } from './support/keycloakAdmin';
import { fillSecret, stage, submitOtp, submitPassword } from './support/login';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { base32Of, freshOtp, nextPeriodOtp } from './support/totp';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

test.describe.configure({ mode: 'serial', retries: 0 });
// 未認証から始める。秘密を入力するので成果物を残さない（上の注記）。
test.use({
  storageState: { cookies: [], origins: [] },
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

/** 必須アクションの画面（登録・更新）の path。Keycloak は登録の画面をこの path か authenticate の path で描く。 */
const REQUIRED_ACTION_PATH = '/login-actions/required-action';

let admin: KeycloakAdmin | null = null;
let userId = '';
let username = '';
let password = '';
/** 登録の画面の hidden `totpSecret`（生の文字列）。2 度目のログインの OTP に使う。 */
let rawSecret = '';

test.beforeAll(async ({}, testInfo) => {
  admin = await openKeycloakAdmin(cfg, REALM);
  username = e2eName(cfg.runId, 'totp-enroll', testInfo.retry);
  // パスワードの方針（12 文字以上・3 種以上）を満たす使い捨ての値。作る前に記録する（成果物の走査が探す）。
  password = `${randomBytes(18).toString('base64url')}Aa1-`;
  recordIssuedSecret(cfg, 'E2E_ISSUED_ENROLL_PASSWORD', password);
  userId = await admin.createUser({
    username,
    email: `${username}@example.invalid`,
    firstName: 'E2E',
    lastName: 'Enroll',
    enabled: true,
    emailVerified: true,
    credentials: [{ type: 'password', value: password, temporary: false }],
  });
});

test.afterAll(async () => {
  if (admin) {
    try {
      if (userId) await admin.deleteUser(userId);
    } finally {
      await admin.close();
    }
  }
});

/** BFF のログインの入口から Keycloak のログイン画面へ進み、パスワードを送る。 */
async function startLogin(page: Page): Promise<void> {
  await page.goto('/bff/auth/login?returnUrl=%2F');
  await page.waitForURL((url) => url.origin === cfg.kcUrl);
  await submitPassword(page, username, password);
}

/**
 * 元の画面（エッジ）へ戻るのを待つ。必須アクションの画面で止まったら、待ち続けずにフォームの id を添えて落とす
 * （登録の後にリカバリーコードの画面が出るようになったときも、ここが知らせる）。
 */
async function expectBackOnEdge(page: Page): Promise<void> {
  await page.waitForURL(
    (url) =>
      (url.origin === cfg.edgeUrl && !url.pathname.startsWith('/bff/')) ||
      (url.origin === cfg.kcUrl && url.pathname.includes(REQUIRED_ACTION_PATH)),
  );
  const forms = await page
    .locator('form[id]')
    .evaluateAll((els) => els.map((e) => String((e as unknown as { id: string }).id)));
  expect(
    new URL(page.url()).origin,
    `元の画面へ戻らず Keycloak の必須アクションの画面で止まった（フォーム: ${forms.join(', ') || '不明'}）`,
  ).toBe(cfg.edgeUrl);
  const me = await page.request.get('/bff/auth/me');
  expect(me.status(), '/bff/auth/me（ログインの後）').toBe(200);
  expect(((await me.json()) as { subject?: string }).subject, 'ログインした利用者').toBe(userId);
}

/** BFF のセッションをログアウトで失効させる（作ったセッションを残さない）。 */
async function signOut(page: Page): Promise<void> {
  const me = await page.request.get('/bff/auth/me', { failOnStatusCode: false });
  if (me.status() !== 200) return;
  const logoutUrl = ((await me.json()) as { logoutUrl?: string | null }).logoutUrl;
  if (logoutUrl) await page.goto(logoutUrl, { timeout: 30_000 });
}

// NFR-10, SC-14, ADR-0026: M-005（前半。リカバリーコードは上の注記）。
test('M-005: TOTP 未登録の利用者はパスワードの後に初回登録へ誘導され、QR・手動キー・デバイス名・確認コードで登録して元の画面へ戻る', async ({
  page,
}, testInfo) => {
  const run = <T>(name: string, fn: (notes: string[]) => Promise<T>) =>
    stage(testInfo, page, cfg.kcUrl, name, fn);
  try {
    await run('作成直後の必須アクション', async () => {
      // realm の既定（CONFIGURE_TOTP の defaultAction）だけが付く。新しい利用者が実際に受ける誘導である。
      expect((await admin!.getUser(userId)).requiredActions ?? []).toEqual(['CONFIGURE_TOTP']);
      expect(await admin!.credentialTypes(userId)).toEqual(['password']);
    });

    await run('ログイン画面', () => startLogin(page));

    await run('初回登録の画面（QR）', async () => {
      // 登録の画面（keycloak.v2 の login-config-totp.ftl）。OTP の入力画面（#otp）ではない。
      await expect(page.locator('#kc-totp-settings-form')).toBeVisible();
      await expect(page.locator('#otp')).toHaveCount(0);
      const qr = page.locator('#kc-totp-secret-qr-code');
      await expect(qr).toBeVisible();
      // 🔴 src（QR は秘密を含む otpauth の URI を符号化している）を期待値の比較に渡さない（失敗の文言に載る）。真偽で見る。
      expect(
        ((await qr.getAttribute('src')) ?? '').startsWith('data:image/png;base64,'),
        'QR コードは PNG の data URI',
      ).toBe(true);
      expect(
        await qr.evaluate((el) => (el as unknown as { naturalWidth: number }).naturalWidth),
        'QR コードの画像が描かれている',
      ).toBeGreaterThan(0);
      rawSecret = await page.locator('#totpSecret').inputValue();
      expect(rawSecret.length, '登録に使う秘密（hidden の totpSecret）').toBeGreaterThanOrEqual(16);
      recordIssuedSecret(cfg, 'E2E_ISSUED_ENROLL_TOTP_SECRET', rawSecret);
      recordIssuedSecret(cfg, 'E2E_ISSUED_ENROLL_TOTP_KEY', base32Of(rawSecret));
    });

    await run('初回登録の画面（手動入力キー）', async () => {
      // QR を読めない利用者の経路（「スキャンできませんか？」）。同じ登録の秘密を base32 で表示する。
      await page.locator('#mode-manual').click();
      const key = page.locator('#kc-totp-secret-key');
      await expect(key).toBeVisible();
      const shown = (await key.innerText()).replace(/\s+/g, '');
      // 🔴 値どうしを期待値の比較に渡さない（失敗の文言に載る）。真偽で見る。
      expect(
        shown === base32Of(rawSecret),
        '手動入力キーが登録に使う秘密の base32 と一致する',
      ).toBe(true);
      expect(
        (await page.locator('#totpSecret').inputValue()) === rawSecret,
        '手動の表示へ切り替えても秘密は変わらない',
      ).toBe(true);
      // 方式・桁・周期（realm の otpPolicy。ADR-0026）を表示する。
      await expect(page.locator('#kc-totp-digits')).toContainText('6');
      await expect(page.locator('#kc-totp-period')).toContainText('30');
    });

    await run('初回登録の画面（デバイス名・確認コード）', async () => {
      await page.locator('#userLabel').fill(`${username}-device`);
      await fillSecret(page.locator('#totp'), await freshOtp(rawSecret));
      await page.locator('#saveTOTPBtn').click();
    });

    await run('元の画面へ戻る', () => expectBackOnEdge(page));

    await run('登録の結果', async () => {
      // 登録で必須アクションが外れ、OTP の資格情報が 1 つ付いた。
      expect((await admin!.getUser(userId)).requiredActions ?? []).toEqual([]);
      expect(await admin!.credentialTypes(userId)).toEqual(['otp', 'password']);
    });
  } finally {
    await signOut(page);
  }
});

// NFR-10, SC-14, ADR-0026: 登録は 1 回だけ。2 度目のログインは登録した TOTP の入力だけを求める。
test('M-005: 登録を終えた利用者の 2 度目のログインは、初回登録の画面を出さず、登録した TOTP の 6 桁だけで通る', async ({
  page,
}, testInfo) => {
  const run = <T>(name: string, fn: (notes: string[]) => Promise<T>) =>
    stage(testInfo, page, cfg.kcUrl, name, fn);
  try {
    await run('ログイン画面', () => startLogin(page));
    await run('OTP 画面', async () => {
      await expect(page.locator('#kc-totp-settings-form')).toHaveCount(0);
      // otpPolicyCodeReusable: false。前の test が使った周期の 6 桁は通らないので、次の周期を待つ。
      await submitOtp(page, (await nextPeriodOtp(rawSecret)).code);
    });
    await run('元の画面へ戻る', () => expectBackOnEdge(page));
  } finally {
    await signOut(page);
  }
});
