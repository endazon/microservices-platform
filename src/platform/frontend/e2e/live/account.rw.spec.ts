// NFR / NFR-12 / SC-13 / SC-15 / SC-16 / ADR-0026 / ADR-0045 / IADR-0632 / IADR-0634（#2034 / E-54）: live-rw の**リセット申請**と
// **アカウントコンソール**。網羅表（#2013）の M-006（申請の存在秘匿とメール）と M-007（個人情報・デバイスの活動）を `e2e-revocable` で流す。
//
// - 🔴 **順序**: このファイルは `live-rw` で流れる（`live-ro` の auth-errors の後、`live-rw-revocation` の前。IADR-0630 決定 4・IADR-0632）。
// - 🔴 **M-006 はパスワードを変えない**（申請 → メールが届くまで。リンクは開かない。IADR-0634 決定 2）。後の revocation と次の実行が使う
//   資格情報はそのまま。リセットリンクと鍵は読んだ直後に発行値として記録し（成果物の走査が探す）、ログ・attach・例外の文言へ出さない（決定 3）。
// - 🔴 秘密（パスワード・OTP・リセットリンク）を扱うので trace・スクリーンショット・動画を切る。OTP は次の周期を待ち、誤りは送らない。
// - **M-007 は読み取り中心**（決定 6）。書き込みは、この試験が自分で作ったセッション B の個別サインアウトだけ。終わりに A・B を
//   Keycloak のログアウトで閉じる（再試行で前の試行のセッションが残らない）。
// - mailpit は kubectl で読むだけ（`support/mailpit.ts`。context は `k3d-<E2E_STACK>` に固定。決定 4）。
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { expect, test, type Browser, type Page, type Response } from '@playwright/test';
import { e2eName } from './support/cleanup';
import { credentialsOf, parseLiveEnv, stateFile, type Role } from './support/env';
import { recordIssuedSecret } from './support/issuedSecrets';
import { stage, submitOtp, submitPassword } from './support/login';
import { latestMailIds, mailCount, readMail, type CapturedMail } from './support/mailpit';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { nextPeriodOtp } from './support/totp';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

const SUBJECT: Role = 'revocable';
const USERNAME = `e2e-${SUBJECT}`;

const ROOT = new URL('../../../../../', import.meta.url);

// realm 宣言（単一情報源。値を書き写さない）。
const REALM = JSON.parse(
  readFileSync(new URL('deploy/keycloak/microservices-platform-realm.json', ROOT), 'utf8'),
) as {
  realm: string;
  users: { username: string; email?: string; attributes?: Record<string, unknown> }[];
  clients: { clientId: string; name?: string }[];
};
const SUBJECT_USER = REALM.users.find((u) => u.username === USERNAME);
const SUBJECT_EMAIL = SUBJECT_USER?.email ?? '';
/** 管理者が付けた属性（realm 宣言の `attributes`。`clearance` など）。本人には見えない・編集できない。 */
const SUBJECT_ADMIN_ATTRIBUTES = Object.keys(SUBJECT_USER?.attributes ?? {});
const BFF_CLIENT_NAME = REALM.clients.find((c) => c.clientId === 'bff')?.name ?? '';
const ACCOUNT_PATH = `/realms/${REALM.realm}/account/`;

/** platform のログインテーマの文言（`messages_ja.properties`。計画が定めた語だけを上書きしている）。 */
function themeMessage(key: string): string {
  const text = readFileSync(
    new URL('deploy/keycloak/themes/platform/login/messages/messages_ja.properties', ROOT),
    'utf8',
  );
  const line = text.split(/\r?\n/).find((l) => l.startsWith(`${key}=`));
  if (!line) throw new Error(`テーマの文言 ${key} が無い`);
  return line.slice(key.length + 1).trim();
}

// SC-15 のメールの判定（純関数。判定を複製しない）。
const resetMail = createRequire(import.meta.url)(
  '../../../../../scripts/check-password-reset-mail.js',
) as {
  linkLifetimeMinutes: (realm: unknown) => number | null;
  extractUrls: (text: string) => string[];
  decodeEntities: (s: string) => string;
  evaluateResetMail: (input: {
    requestStatus: number;
    delivered: number;
    recipients: string[];
    expectedEmail: string;
    subject: string;
    text: string;
    attachments: number;
    lifetimeMinutes: number | null;
    issuerBase: string;
  }) => string[];
};

/** アカウントコンソール（keycloak.v3）の文言。親テーマ（Keycloak 26.7.4）の ja の訳。 */
const ACCOUNT_T = {
  signOut: 'サインアウト',
  signOutWarning: 'セッションからサインアウトしますか？',
  confirm: '確認',
  currentSession: '現在のセッション',
};

/** 申請の画面の送信ボタン（親テーマの `doSubmit`。platform テーマは上書きしていない）。 */
const RESET_SUBMIT = '送信';

test.describe.configure({ mode: 'serial' });
test.use({
  storageState: stateFile(cfg.authDir, 'admin'),
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

async function freshContext(browser: Browser) {
  return browser.newContext({
    storageState: { cookies: [], origins: [] },
    baseURL: cfg.edgeUrl,
    ignoreHTTPSErrors: cfg.insecureTls,
    locale: 'ja-JP',
  });
}

/** 申請の画面の後の表示を、入れた利用者名だけ伏せて返す（存在秘匿の比較。値は秘密ではない）。 */
async function shownAfterReset(page: Page, username: string): Promise<string> {
  const body = await page.locator('body').innerText();
  return body.split(username).join('<username>').replace(/\s+/g, ' ').trim();
}

/**
 * 🔴 メール本文のリセットリンクと鍵（`key`）を発行値として記録する（成果物の走査が探す。IADR-0634 決定 3）。値は返さない・出さない。
 * 鍵の無いリンクは記録しない（判定は `evaluateResetMail` が持つ）。
 */
function recordResetLinks(mail: CapturedMail): void {
  const links = resetMail
    .extractUrls(resetMail.decodeEntities(mail.text))
    .filter((url) => url.includes('login-actions/action-token'));
  for (const url of links) {
    recordIssuedSecret(cfg, 'E2E_ISSUED_RESET_LINK', url);
    const key = new URL(url).searchParams.get('key');
    if (key) recordIssuedSecret(cfg, 'E2E_ISSUED_RESET_KEY', key);
  }
}

/**
 * 実在の 1 通が届いた後に件数を見続ける**静かな窓**（IADR-0634 決定 5）。届く順には頼らない —— 非実在の側の送出が
 * 遅れて届いても、この窓のあいだに件数が増えれば落ちる。長さは、既存の存在秘匿の検査（`check-password-reset-mail.js` の T-10）が
 * 非実在の申請の後に置く待ち（3 秒）の 3 倍を超える 10 秒にする。非実在の申請は実在の申請より前に出しているので、
 * 非実在の側が待たれる時間は、実在の申請と受け取りにかかった時間の分だけさらに長い。
 */
const QUIET_WINDOW_MS = 10_000;
const QUIET_POLL_MS = 1_000;

/** 静かな窓のあいだ、件数が `expected` から動かないことを確かめ、最後の件数を返す。 */
async function mailCountAfterQuietWindow(expected: number): Promise<number> {
  const deadline = Date.now() + QUIET_WINDOW_MS;
  let count = mailCount(cfg);
  while (Date.now() < deadline) {
    expect(
      count,
      `静かな窓（${QUIET_WINDOW_MS / 1000} 秒）のあいだ、捕捉用 MTA の件数は実在の 1 通だけ増えたまま`,
    ).toBe(expected);
    await new Promise((resolve) => setTimeout(resolve, QUIET_POLL_MS));
    count = mailCount(cfg);
  }
  expect(count, '静かな窓の終わりの件数').toBe(expected);
  return count;
}

/** SC-13 のログイン画面から SC-15 の申請を 1 回出し、申請後の表示を返す。 */
async function requestReset(page: Page, username: string): Promise<string> {
  await page.goto('/login');
  await page.getByRole('button', { name: 'Keycloak でサインイン', exact: true }).click();
  await page.waitForURL((url) => url.origin === cfg.kcUrl);
  await expect(page.locator('#kc-form-login')).toBeVisible();
  await page.getByRole('link', { name: themeMessage('doForgotPassword'), exact: true }).click();
  await page.locator('#username').fill(username);
  await page.getByRole('button', { name: RESET_SUBMIT, exact: true }).click();
  await expect(page.getByText(themeMessage('emailSentMessage'), { exact: true })).toBeVisible();
  return shownAfterReset(page, username);
}

/**
 * Keycloak の応答のうち失敗（4xx・5xx）と、届かなかった要求を、段の診断（`notes`）と `failures` へ積む（#2034 の調べ。
 * オーケストレーターの条件 2026-10-25）。載せるのは method・path（query は捨てる —— `code`・`state`・`session_state` を残さない）・
 * status と、JSON の `error`・`error_description`（OAuth の誤りの種類。秘密ではない）だけ。本文・header の値は載せない。
 * 🔴 コールバックに載る認可コードは、念のため発行値として記録する（成果物の走査が探す）。
 */
function watchKeycloakFailures(
  page: Page,
  notes: string[],
  failures: string[],
  seen: string[],
): void {
  page.on('request', (r) => {
    const url = new URL(r.url());
    const code = url.origin === cfg.kcUrl ? url.searchParams.get('code') : null;
    if (code && code.length >= 12) recordIssuedSecret(cfg, 'E2E_ISSUED_AUTH_CODE', code);
  });
  page.on('response', (r) => {
    const url = new URL(r.url());
    // 🔴 origin で絞らない（#2034 の調べ: kcUrl に限ると、別の origin へ向いた要求の失敗を取りこぼした）。
    const line = `${r.request().method()} ${url.origin}${url.pathname} → ${r.status()}`;
    // #2034 の調べ（3 回目）: 4xx/5xx も届かなかった要求も 0 件だったので、fetch / XHR の応答は status によらず残す
    // （method・origin＋path・status だけ。query と本文は読まない）。
    if (['fetch', 'xhr'].includes(r.request().resourceType())) seen.push(line);
    if (r.status() < 400) return;
    void r
      .json()
      .then(
        (body: { error?: unknown; error_description?: unknown }) =>
          `${line} (error=${String(body.error ?? '')} / ${String(body.error_description ?? '')})`,
      )
      .catch(() => line)
      .then((entry) => {
        failures.push(entry);
        notes.push(entry);
      });
  });
  page.on('requestfailed', (r) => {
    const url = new URL(r.url());
    const entry = `request failed: ${r.method()} ${url.origin}${url.pathname} (${r.failure()?.errorText ?? ''})`;
    failures.push(entry);
    notes.push(entry);
  });
  // keycloak-js と account-ui の初期化の例外は console.error に出る。🔴 query・fragment と JWT の形は伏せ、300 文字で切る。
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    seen.push(`console.error: ${redact(m.text())}`);
  });
  page.on('pageerror', (e) => seen.push(`pageerror: ${redact(e.message)}`));
}

/** 診断の文字列から秘密になりうる部分（URL の query・fragment、JWT の形）を伏せる。 */
function redact(text: string): string {
  return text
    .replace(/eyJ[\w-]+\.[\w-]+\.[\w-]*/g, '<jwt>')
    .replace(/([?#])[^\s"'<>)]*/g, '$1<redacted>')
    .slice(0, 300);
}

/**
 * アカウントコンソールへ新しくログインする（パスワード → 次の周期の OTP）。個人情報の応答を返す。
 * コンソールが初期化に失敗して誤りの画面を出したら、個人情報の応答を待たずに落とす（`notes` に Keycloak の失敗の応答が載る）。
 */
async function loginToAccount(page: Page, notes: string[]): Promise<unknown> {
  const creds = credentialsOf(SUBJECT, process.env);
  const failures: string[] = [];
  const seen: string[] = [];
  watchKeycloakFailures(page, notes, failures, seen);
  const profile = page.waitForResponse(
    (r) =>
      r.request().method() === 'GET' &&
      new URL(r.url()).pathname.replace(/\/$/, '') === ACCOUNT_PATH.replace(/\/$/, '') &&
      new URL(r.url()).searchParams.has('userProfileMetadata'),
    { timeout: 90_000 },
  );
  notes.push('step: アカウントコンソールを開く');
  await page.goto(`${cfg.kcUrl}${ACCOUNT_PATH}`);
  notes.push('step: パスワード');
  await submitPassword(page, USERNAME, creds.password);
  const { code } = await nextPeriodOtp(creds.totpSecret);
  notes.push('step: OTP');
  await submitOtp(page, code);
  await page.waitForURL(
    (url) => url.origin === cfg.kcUrl && url.pathname.startsWith(ACCOUNT_PATH),
    {
      waitUntil: 'commit',
    },
  );
  notes.push('step: アカウントコンソールへ戻った（個人情報の応答か誤りの画面を待つ）');
  const consoleError = page
    // 誤りの画面は Alert ではなく、danger の題のアイコンを持つ Modal（keycloak-ui-shared の ErrorPage）。#2034 の調べで
    // Alert の class に固定して外した。アカウントコンソールは開いた直後に dialog を出さないので、dialog が出たら誤りの画面とみなす。
    .getByRole('dialog')
    .waitFor({ state: 'visible', timeout: 90_000 })
    .then(() => 'error' as const)
    // 個人情報が先に届いた後の時間切れは、待っていないだけ（落とさない）。
    .catch(() => 'none' as const);
  // 誤りの画面が先なら個人情報の待ちは捨てる（後の時間切れで落とさない）。
  profile.catch(() => undefined);
  const first = await Promise.race([profile, consoleError]);
  // 失敗の応答の本文（error の種類）を読み終えるのを少し待ってから文言に添える。
  await new Promise((resolve) => setTimeout(resolve, 500));
  // 添付（failed-stage）は reporter が切り詰めるので、切り分けに要る材料は例外の文言に入れる。
  const detail = [
    `段: ${notes.filter((n) => n.startsWith('step:')).join(' > ')}`,
    `失敗の応答・届かなかった要求: ${failures.join(' | ') || '（なし）'}`,
    `fetch/xhr の応答と console のエラー: ${seen.slice(-40).join(' | ') || '（なし）'}`,
  ].join('\n');
  if (first === 'none') {
    throw new Error(`個人情報の応答も誤りの画面も 90 秒のうちに出なかった。\n${detail}`);
  }
  if (first === 'error') {
    throw new Error(`アカウントコンソールが初期化に失敗した（誤りの画面）。\n${detail}`);
  }
  expect(first.status(), 'GET account（個人情報）').toBe(200);
  return first.json();
}

interface Devices {
  sessions: { id: string; current?: boolean }[];
}

function devicesResponse(page: Page): Promise<Response> {
  return page.waitForResponse(
    (r) =>
      r.request().method() === 'GET' &&
      new URL(r.url()).pathname === `${ACCOUNT_PATH}sessions/devices`,
  );
}

async function openDeviceActivity(page: Page): Promise<{ current: string; all: string[] }> {
  const res = devicesResponse(page);
  await page.goto(`${cfg.kcUrl}${ACCOUNT_PATH}account-security/device-activity`);
  const r = await res;
  expect(r.status(), 'GET account/sessions/devices').toBe(200);
  const sessions = ((await r.json()) as Devices[]).flatMap((d) => d.sessions);
  const current = sessions.filter((s) => s.current).map((s) => s.id);
  expect(current, '現在のセッションはちょうど 1 つ').toHaveLength(1);
  await expect(page.getByText(ACCOUNT_T.currentSession, { exact: true })).toBeVisible();
  return { current: current[0]!, all: sessions.map((s) => s.id) };
}

/** Keycloak のログアウト（RP 起点ではない確認画面）で、そのセッションを閉じる。後始末なので失敗しても試験を落とさない。 */
async function keycloakLogout(page: Page): Promise<void> {
  try {
    await page.goto(`${cfg.kcUrl}/realms/${REALM.realm}/protocol/openid-connect/logout`);
    const confirm = page.locator('#kc-logout');
    if (await confirm.isVisible({ timeout: 5_000 }).catch(() => false)) await confirm.click();
  } catch {
    // 後始末。セッションは revocation の無効化でも失効する。
  }
}

// SC-15, NFR-12: M-006。存在秘匿（文言が同一）・実在の側にだけ 1 通・本文はリンクと有効期限だけ。パスワードは変えない。
test(
  'M-006: 実在（e2e-revocable）と非実在の利用者名でリセットを申請すると文言が同一で、メールは実在の側にだけ 1 通届き、本文はリンクと有効期限だけ',
  { tag: ['@p1', '@live-rw'] },
  async ({ browser }, testInfo) => {
    test.setTimeout(120_000);
    expect(SUBJECT_EMAIL, `${USERNAME} の登録アドレスが realm 宣言に無い`).not.toBe('');
    const absent = e2eName(cfg.runId, 'absent', testInfo.retry);
    const lifetimeMinutes = resetMail.linkLifetimeMinutes(REALM);

    const ctx = await freshContext(browser);
    const page = await ctx.newPage();
    try {
      const before = mailCount(cfg);

      // 非実在を先に出す（決定 5。どちらの順でも、下の静かな窓で非実在の側の送出を待ち切る）。
      const shownAbsent = await stage(testInfo, page, cfg.kcUrl, '非実在の申請', () =>
        requestReset(page, absent),
      );
      const shownPresent = await stage(testInfo, page, cfg.kcUrl, '実在の申請', () =>
        requestReset(page, USERNAME),
      );
      expect(shownPresent, '申請後の表示が実在／非実在で同一（存在秘匿）').toBe(shownAbsent);

      // 実在の 1 通が届くのを待つ（SMTP の受け取りは要求の後に終わることがある）。
      await expect(async () => {
        expect(mailCount(cfg), '捕捉用 MTA の件数').toBeGreaterThan(before);
      }).toPass({ timeout: 30_000, intervals: [1_000, 2_000, 3_000] });
      // 🔴 届いた後も静かな窓のあいだ件数を見続け、遅れて届く送出（非実在の側）を取りこぼさない（決定 5）。
      const after = await mailCountAfterQuietWindow(before + 1);
      const mails: CapturedMail[] = latestMailIds(cfg, after - before).map((id) =>
        readMail(cfg, id),
      );

      // 🔴 判定の前に、届いた本文のリセットリンクと鍵を発行値として記録する（値は出さない。決定 3）。
      for (const mail of mails) recordResetLinks(mail);

      // 宛先と件数だけを出す（本文は出さない）。
      expect(
        mails.map((m) => m.recipients.join(',')),
        '届いたのは実在の側の 1 通だけ（非実在の側は 0 通）',
      ).toEqual([SUBJECT_EMAIL]);
      const mail = mails[0]!;
      const failures = resetMail.evaluateResetMail({
        // 申請の成否は、上の画面の文言（`emailSentMessage`）で既に見ている。ブラウザのフォーム送信は応答のステータスを
        // 返さないので、ここは固定値で渡し、判定器の他の軸（件数・宛先・本文・添付）だけを効かせる。
        requestStatus: 200,
        delivered: mails.length,
        recipients: mail.recipients,
        expectedEmail: SUBJECT_EMAIL,
        subject: mail.subject,
        text: resetMail.decodeEntities(mail.text),
        attachments: mail.attachments,
        lifetimeMinutes,
        issuerBase: cfg.kcUrl,
      });
      expect(failures, 'リセットのメール（リンクと有効期限だけ。ADR-0045 決定 7）').toEqual([]);
    } finally {
      await ctx.close();
    }
  },
);

// SC-16: M-007。個人情報は見るだけ（保存しない）。セッションの個別サインアウトは、この試験が作った B だけ。
test(
  'M-007: e2e-revocable のアカウントコンソールで、利用者名と属性は本人に編集できず、デバイスの活動から自分の別セッションだけを個別にサインアウトできる',
  { tag: ['@p2', '@live-rw'] },
  async ({ browser, page: admin }, testInfo) => {
    test.setTimeout(180_000);
    const ctxA = await freshContext(browser);
    const ctxB = await freshContext(browser);
    const a = await ctxA.newPage();
    const b = await ctxB.newPage();
    try {
      await test.step('個人情報: 利用者名は読み取り専用、属性（clearance など）は本人に見えない', () =>
        stage(testInfo, a, cfg.kcUrl, 'セッション A のログイン', async (notes) => {
          expect(SUBJECT_ADMIN_ATTRIBUTES, `${USERNAME} の属性が realm 宣言に無い`).not.toEqual([]);
          expect(
            BFF_CLIENT_NAME,
            'realm 宣言に bff クライアントの表示名が無い（B の行を特定できない）',
          ).not.toBe('');
          const profile = (await loginToAccount(a, notes)) as {
            username?: string;
            attributes?: Record<string, unknown>;
            userProfileMetadata?: { attributes?: { name: string; readOnly?: boolean }[] };
          };
          expect(profile.username).toBe(USERNAME);
          const meta = profile.userProfileMetadata?.attributes ?? [];
          expect(
            meta.find((m) => m.name === 'username')?.readOnly,
            '利用者名は本人に編集不可（editUsernameAllowed: false）',
          ).toBe(true);
          // 属性は管理者だけが編集する（unmanagedAttributePolicy: ADMIN_EDIT）。本人の面には名前も値も出ない。
          for (const name of SUBJECT_ADMIN_ATTRIBUTES) {
            expect(
              meta.map((m) => m.name),
              `属性 ${name} が本人の面に出ない`,
            ).not.toContain(name);
            expect(Object.keys(profile.attributes ?? {})).not.toContain(name);
          }
          await expect(a.locator('#username')).toHaveValue(USERNAME);
          await expect(a.locator('#username')).not.toBeEditable();
          for (const name of SUBJECT_ADMIN_ATTRIBUTES) {
            await expect(a.locator(`[id="${name}"], [name="${name}"]`)).toHaveCount(0);
          }
        }));

      const sessionB = await test.step('自分の別セッション B を作る', () =>
        stage(testInfo, b, cfg.kcUrl, 'セッション B のログイン', async (notes) => {
          await loginToAccount(b, notes);
          return (await openDeviceActivity(b)).current;
        }));

      await test.step('デバイスの活動: B だけを個別にサインアウトする（A と他の役割は残る）', () =>
        stage(testInfo, a, cfg.kcUrl, 'B の個別サインアウト', async () => {
          const listed = await openDeviceActivity(a);
          expect(listed.all, 'B がデバイスの活動に出る').toContain(sessionB);
          // B の行: 現在でなく、BFF のクライアントを含まない行（アカウントコンソールだけのセッション）。
          const rows = a
            .locator('.signed-in-device-list [class*="data-list__item-row"]')
            .filter({ has: a.getByRole('button', { name: ACCOUNT_T.signOut, exact: true }) })
            .filter({ hasNotText: BFF_CLIENT_NAME });
          await expect(rows, 'アカウントコンソールだけの別セッションは B の 1 つ').toHaveCount(1);
          await rows.getByRole('button', { name: ACCOUNT_T.signOut, exact: true }).click();
          const dialog = a.getByRole('dialog');
          await expect(dialog.getByText(ACCOUNT_T.signOutWarning)).toBeVisible();
          const deleted = a.waitForResponse(
            (r) =>
              r.request().method() === 'DELETE' &&
              new URL(r.url()).pathname === `${ACCOUNT_PATH}sessions/${sessionB}`,
          );
          const refreshed = devicesResponse(a);
          await dialog.getByRole('button', { name: ACCOUNT_T.confirm, exact: true }).click();
          expect((await deleted).status(), 'DELETE account/sessions/{B}').toBeLessThan(300);
          const after = ((await (await refreshed).json()) as Devices[]).flatMap((d) => d.sessions);
          expect(after.map((s) => s.id)).not.toContain(sessionB);
          expect(after.find((s) => s.current)?.id, '現在のセッション A は残る').toBe(
            listed.current,
          );
        }));

      await test.step('B の画面はログインへ倒れ、他の役割（e2e-admin の BFF のセッション）は残る', async () => {
        await b.goto(`${cfg.kcUrl}${ACCOUNT_PATH}`);
        await expect(b.locator('#kc-form-login')).toBeVisible({ timeout: 30_000 });
        expect(
          (await admin.request.get('/bff/auth/me')).status(),
          '/bff/auth/me（e2e-admin）',
        ).toBe(200);
      });
    } finally {
      // 後始末: この試験が作ったセッションを閉じる（再試行で前の試行のセッションが B の行と紛れない）。
      await keycloakLogout(a);
      await keycloakLogout(b);
      await ctxA.close();
      await ctxB.close();
    }
  },
);
