// NFR-10 / NFR-13 / SC-13 / SC-14 / ADR-0026 / ADR-0032 / IADR-0624 / IADR-0626（#2027 / E-40）: live-ro の**ログインの異常系と
// ログアウト**。網羅表（#2013）の M-003（誤りのパスワード・存在しない ID で同じ固定文言）、M-004（OTP の不一致で再入力を求め、
// 正しい 6 桁で通る）、M-010（ログアウトで cookie が消え、保護画面がリロードで出ない）。
//
// 🔴 IADR-0626 の決定（網羅表の役割から変えた理由を含む）:
// - **被験者は `e2e-revocable`**（網羅表の `e2e-noaccess`・`e2e-user` ではない）。ログアウトは BFF の backchannel logout で
//   **その利用者の全セッション**を失効させる（IADR-0273 決定 2）ので、他の live-ro の spec が並列に使う役割で流すと、その役割の
//   storageState を巻き添えで失効させる。失敗の回数（brute force の数え）もアカウントの状態であり、基準書 §5 のとおり状態を
//   変える試験は専用の `e2e-revocable` で行う。
// - **誤りは 1 回ずつ**（誤りのパスワード 1・誤りの OTP 1。存在しない ID は利用者が無いので数えられない）、**直後に正しい入力で
//   ログインして数えを戻す**（brute force は 5 回失敗で 900 秒）。`retries: 0`・直列。
// - この spec は秘密（パスワード・OTP）を入力するので、**trace・スクリーンショット・動画を切る**（`live-setup` と同じ理由。trace は
//   フォームの POST 本文とセッションの `Set-Cookie` を記録する）。失敗した段は文字で attach する（`support/login.ts` の `stage`）。
// - OTP は**次の周期を待ってから**出す（`otpPolicyCodeReusable: false`。`live-setup` が同じ周期に使った 6 桁は 2 度通らない）。
//   待つ間に 1 秒以上空くので、誤りが 1 秒以内に続いたときの短時間ロック（Keycloak の quick login check）にも掛からない。
// - 作ったセッションはこの test の中でログアウトして失効させる（途中で落ちたら finally で失効させる）。
import { randomBytes } from 'node:crypto';
import { expect, request, test, type Page } from '@playwright/test';
import { credentialsOf, parseLiveEnv, type Role } from './support/env';
import { stage, submitOtp, submitPassword } from './support/login';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { nextPeriodOtp } from './support/totp';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

const ROLE: Role = 'revocable';
const USERNAME = `e2e-${ROLE}`;
/** realm のテーマ（`deploy/keycloak/themes/platform/login/messages/messages_ja.properties`）の固定文言。存在秘匿で実在・非実在に同じく出る。 */
const INVALID_USER = '社員ID またはパスワードが正しくありません';
/** 親テーマ（Keycloak の base）の ja の文言。 */
const INVALID_OTP = '無効なオーセンティケーターコードです';
/**
 * 欄の誤りを出す要素（1 つ）。DOM の根拠: Keycloak 26.7.4 の keycloak.v2 `login/field.ftl` 17 行（外側 `#input-error-container-<name>`）と
 * 21 行（内側 `#input-error-<name>`。入れ子なので `[id^="input-error"]` は 2 要素に当たる）、`login.ftl` 20 行（パスワードの誤りは
 * name=username の欄に出る）、`login-otp.ftl` 30 行（name=otp）。同じ版のイメージで CI が通る `e2e/keycloak/sc13-16-theme-a11y.kc.ts` も `#input-error-otp` を引く。
 */
const USERNAME_ERROR = '#input-error-username';
const OTP_ERROR = '#input-error-otp';
const SESSION_COOKIE = '__Host-msp-session';

test.describe.configure({ mode: 'serial', retries: 0 });
// 未認証から始める（基準書 §5）。秘密を入力するので成果物を残さない（上の注記）。
test.use({
  storageState: { cookies: [], origins: [] },
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

/** Keycloak のフォームを送り、その応答（再描画）を待つ。同じ文言が前の試行から残っているのを今回の結果と取り違えない。 */
async function submitAndWait(page: Page, submit: () => Promise<void>) {
  const posted = page.waitForResponse(
    (r) => r.request().method() === 'POST' && new URL(r.url()).origin === cfg.kcUrl,
  );
  await submit();
  await posted;
}

/** BFF のセッションが生きていればログアウトで失効させる（途中で落ちた test の後始末。値は扱わない）。 */
async function revokeIfSignedIn(page: Page) {
  const me = await page.request.get('/bff/auth/me', { failOnStatusCode: false });
  if (me.status() !== 200) return;
  const logoutUrl = ((await me.json()) as { logoutUrl?: string | null }).logoutUrl;
  if (logoutUrl) await page.goto(logoutUrl, { timeout: 30_000 });
}

// NFR-13, SC-13: M-003 / NFR-10, SC-14: M-004 / ADR-0032: M-010。
test(
  'M-003・M-004・M-010: 誤りのパスワードと存在しない ID は同じ固定文言で断られ、誤りの OTP は再入力を求められ、正しい入力で元の画面へ戻り、サインアウトすると cookie が消えて保護画面がリロードで出ない',
  { tag: ['@p1', '@live-ro'] },
  async ({ page }, testInfo) => {
    test.setTimeout(120_000);
    // 欠けていれば Keycloak へ送る前に例外（空の値を誤入力として数えさせない）。
    const { password, totpSecret: secret } = credentialsOf(ROLE, process.env);
    // 誤りの値は秘密ではない（乱数で、実の値と一致しない）。
    const wrongPassword = `wrong-${randomBytes(9).toString('base64url')}`;
    const absentUser = `e2e-absent-${randomBytes(6).toString('hex')}`;
    const at = <T>(name: string, run: (notes: string[]) => Promise<T>) =>
      stage(testInfo, page, cfg.kcUrl, name, run);

    try {
      await test.step('保護画面を開くと /login へ誘導され、Keycloak のログイン画面へ進む', () =>
        at('ログインの入口', async () => {
          await page.goto('/my/notes');
          await expect(page).toHaveURL(/\/login(\?|$)/);
          await page.getByRole('button', { name: 'Keycloak でサインイン', exact: true }).click();
          await page.waitForURL((url) => url.origin === cfg.kcUrl);
        }));

      await test.step('M-003: 存在しない ID と、実在の ID の誤りのパスワードは、どちらも同じ固定文言で断られる', () =>
        at('誤りのパスワード', async () => {
          // 存在しない ID は利用者が無いので失敗の数えに入らない。実在の ID の誤りは 1 回だけ。
          for (const username of [absentUser, USERNAME]) {
            await submitAndWait(page, () => submitPassword(page, username, wrongPassword));
            await expect(page.locator(USERNAME_ERROR)).toHaveText(INVALID_USER);
            await expect(page.locator('#kc-form-login')).toBeVisible();
            await expect(page.locator('#otp')).toHaveCount(0);
          }
        }));

      await test.step('M-004: 正しいパスワードで OTP 画面へ進み、誤りの OTP は再入力を求められ、正しい 6 桁で元の画面へ戻る', () =>
        at('OTP 画面', async () => {
          await submitAndWait(page, () => submitPassword(page, USERNAME, password));
          await expect(page.locator('#otp')).toBeVisible();
          // 次の周期を待つ（setup が使った 6 桁を避ける。誤りのパスワードとの間も 1 秒以上空く）。
          const { code, wrong } = await nextPeriodOtp(secret);
          await submitAndWait(page, () => submitOtp(page, wrong));
          await expect(page.locator(OTP_ERROR)).toContainText(INVALID_OTP);
          await expect(page.locator('#otp')).toBeVisible();
          await submitOtp(page, code);
          await page.waitForURL(
            (url) => url.origin === cfg.edgeUrl && url.pathname === '/my/notes',
          );
          await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toBeVisible();
          const me = await page.request.get('/bff/auth/me');
          expect(me.status(), '/bff/auth/me（ログインの後）').toBe(200);
        }));

      await test.step('M-010: サインアウトすると cookie が消え、同じ cookie は BFF で 401 になり、保護画面はリロードで出ない', () =>
        at('サインアウト', async () => {
          // ログアウト前の cookie を手元に持つ（ファイルにも成果物にも書かない）。失効したことを同じ cookie で確かめる。
          const before = await page.context().storageState();
          expect(
            before.cookies.filter((c) => c.name === SESSION_COOKIE),
            'ログイン後のセッション cookie',
          ).toHaveLength(1);

          await page.getByRole('button', { name: 'サインアウト', exact: true }).click();
          await page.waitForURL((url) => url.origin === cfg.edgeUrl && url.pathname === '/login');
          expect(
            (await page.context().cookies()).filter((c) => c.name === SESSION_COOKIE),
            'ログアウト後のセッション cookie',
          ).toEqual([]);

          await page.goto('/my/notes');
          await expect(page).toHaveURL(/\/login(\?|$)/);
          await expect(
            page.getByRole('button', { name: 'Keycloak でサインイン', exact: true }),
          ).toBeVisible();
          await expect(page.getByRole('heading', { name: '個人資料', level: 1 })).toHaveCount(0);

          const probe = await request.newContext({
            storageState: before,
            baseURL: cfg.edgeUrl,
            ignoreHTTPSErrors: cfg.insecureTls,
          });
          try {
            const after = await probe.get('/bff/auth/me', { failOnStatusCode: false });
            expect(after.status(), 'ログアウト前の cookie で /bff/auth/me').toBe(401);
          } finally {
            await probe.dispose();
          }
        }));
    } finally {
      // 途中で落ちたときに、作ったセッションを残さない（成功したときは既にログアウト済みで何もしない）。
      await revokeIfSignedIn(page).catch(() => undefined);
    }
  },
);
