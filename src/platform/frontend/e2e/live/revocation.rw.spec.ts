// NFR / NFR-14 / UC-05 / FR-09 / SC-13 / SC-17 / ADR-0032 / IADR-0626 / IADR-0632（#2033 / E-53）: live-rw の**利用者の無効化と
// 全セッション失効**。網羅表（#2013）の M-009（無効化で別 context の e2e-revocable が 401 → /login）と M-109（再有効化）を、`e2e-admin`（操作する側）と
// 新しくログインした `e2e-revocable`（被験者）で流す。
//
// - 🔴 **順序**: このファイルは専用の project `live-rw-revocation` で流れ、同じ実行の `live-ro`・`live-rw` に依存する（`playwright.live.config.ts`。
//   IADR-0632 決定 1）。無効化はその利用者の全セッションを失効させ、再有効化してもセッションは戻らないので、e2e-revocable の状態を使う他の試験
//   （auth-errors と、E-54 のリセット申請・アカウントコンソール）が終わってから流す。
// - 🔴 e2e-revocable は**新しくログインする**（setup の state は auth-errors のログアウトで失効済み）。OTP は次の周期を待ってから出し、誤りは送らない
//   （brute force の数えを増やさない。決定 2）。秘密を入力するので trace・スクリーンショット・動画を切る（決定 3）。
// - 🔴 **後始末は有効化**（決定 4）。afterEach が API で e2e-revocable の `enabled` を確かめ、偽なら有効化を送り、それでも戻らなければ赤にする。
// - 書き込みは使い捨てのスタック（e2e-live.yml）の中だけ（層 `rw` の門。`support/readiness.ts` の assertDisposableStack）。
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { credentialsOf, parseLiveEnv, stateFile, type Role } from './support/env';
import { stage, submitOtp, submitPassword } from './support/login';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { nextPeriodOtp } from './support/totp';
import { ja } from './support/catalog';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

const SUBJECT: Role = 'revocable';
const USERNAME = `e2e-${SUBJECT}`;

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({
  storageState: stateFile(cfg.authDir, 'admin'),
  trace: 'off',
  screenshot: 'off',
  video: 'off',
});

const T = {
  usersHeading: ja('ユーザーアカウント管理'),
  edit: ja('編集'),
  disable: ja('無効化（全セッション失効）'),
  confirmTitle: ja('このアカウントを無効化しますか？'),
  confirm: ja('無効化する'),
  enable: ja('再有効化'),
  enabledBadge: ja('有効'),
  disabledBadge: ja('無効（全セッション失効）'),
  notesHeading: ja('個人資料'),
};

/** 失効が業務の API にも効くことを見る口（e2e-revocable が読める、認証だけの保護された GET。本人の個人資料の一覧）。 */
const BUSINESS_API = '/bff/private-notes';

/** 無効化の後、BFF のセッションが消えるのを待つ上限（バックチャネルログアウトは非同期に届く）。 */
const REVOKE_WAIT = { timeout: 30_000, intervals: [1_000, 2_000, 5_000] };

interface PlatformUser {
  id: string;
  username: string;
  enabled: boolean;
}

/** e2e-admin の API で e2e-revocable を引く（読み取り）。 */
async function subjectUser(request: APIRequestContext): Promise<PlatformUser> {
  const res = await request.get('/bff/admin/users');
  expect(res.status(), 'GET /bff/admin/users').toBe(200);
  const user = ((await res.json()) as PlatformUser[]).find((u) => u.username === USERNAME);
  expect(user, `${USERNAME} が利用者の一覧に居ない`).toBeDefined();
  return user!;
}

/** 画面の操作が送った BFF の要求の応答を待つ（method と pathname で特定する）。 */
function bffResponse(page: Page, method: string, path: string) {
  return page.waitForResponse(
    (r) => r.request().method() === method && new URL(r.url()).pathname === path,
  );
}

// 🔴 後始末: e2e-revocable を有効へ戻す。戻らなければ赤（決定 4。cleanup.ts は失敗しても test を落とさないので使わない）。
test.afterEach(async ({ page }) => {
  const user = await subjectUser(page.request);
  if (!user.enabled) {
    const res = await page.request.post(`/bff/admin/users/${encodeURIComponent(user.id)}/enable`, {
      headers: { 'X-MSP-CSRF': '1' },
      failOnStatusCode: false,
    });
    expect(res.status(), 'POST /bff/admin/users/{id}/enable（後始末）').toBe(200);
  }
  expect((await subjectUser(page.request)).enabled, `${USERNAME} が有効に戻っていない`).toBe(true);
});

// SC-17, NFR-14, UC-05: M-009（被験者の側）・M-109（管理の側）。状態が続くので 1 つの test で段を踏む。
test(
  'M-009 / M-109: e2e-admin が e2e-revocable を無効化すると、別 context でログインしていた e2e-revocable の /bff/* が 401 になり /login へ倒れ、再有効化すると「有効」に戻る（セッションは戻らない）',
  { tag: ['@p0', '@live-rw'] },
  async ({ page, browser }, testInfo) => {
    test.setTimeout(150_000);
    const before = await subjectUser(page.request);
    expect(before.enabled, `${USERNAME} が始めから無効（前の実行の後始末が効いていない）`).toBe(
      true,
    );

    const subjectContext = await browser.newContext({
      storageState: { cookies: [], origins: [] },
      baseURL: cfg.edgeUrl,
      ignoreHTTPSErrors: cfg.insecureTls,
      locale: 'ja-JP',
    });
    const subject = await subjectContext.newPage();
    const at = <R>(name: string, run: (notes: string[]) => Promise<R>) =>
      stage(testInfo, subject, cfg.kcUrl, name, run);
    try {
      await test.step('e2e-revocable が別 context で新しくログインし、保護画面を開ける', () =>
        at('被験者のログイン', async () => {
          // 欠けていれば Keycloak へ送る前に例外（空の値を誤入力として数えさせない）。
          const creds = credentialsOf(SUBJECT, process.env);
          await subject.goto('/my/notes');
          await expect(subject).toHaveURL(/\/login(\?|$)/);
          await subject.getByRole('button', { name: 'Keycloak でサインイン', exact: true }).click();
          await subject.waitForURL((url) => url.origin === cfg.kcUrl);
          await submitPassword(subject, USERNAME, creds.password);
          await expect(subject.locator('#otp')).toBeVisible();
          // 次の周期を待つ（前の試験が同じ周期に使った 6 桁は 2 度通らない。誤りは送らない）。
          const { code } = await nextPeriodOtp(creds.totpSecret);
          await submitOtp(subject, code);
          await subject.waitForURL(
            (url) => url.origin === cfg.edgeUrl && url.pathname === '/my/notes',
          );
          await expect(
            subject.getByRole('heading', { name: T.notesHeading, level: 1 }),
          ).toBeVisible();
          expect(
            (await subject.request.get('/bff/auth/me')).status(),
            '/bff/auth/me（被験者）',
          ).toBe(200);
        }));

      await page.goto('/admin/users');
      await expect(page.getByRole('heading', { name: T.usersHeading, level: 1 })).toBeVisible();
      const row = page.getByRole('row').filter({ has: page.getByText(USERNAME, { exact: true }) });

      await test.step('M-109: e2e-admin が SC-17 で無効化する（確認を挟む）', async () => {
        await row.getByRole('button', { name: T.edit, exact: true }).click();
        await page.getByRole('button', { name: T.disable, exact: true }).click();
        const dialog = page.getByRole('dialog', { name: T.confirmTitle });
        const disabled = bffResponse(page, 'POST', `/bff/admin/users/${before.id}/disable`);
        await dialog.getByRole('button', { name: T.confirm, exact: true }).click();
        expect((await disabled).status(), 'POST /bff/admin/users/{id}/disable').toBe(200);
        await expect(row.getByText(T.disabledBadge, { exact: true })).toBeVisible();
      });

      await test.step('M-009: 別 context の e2e-revocable の /bff/* は 401 になり、保護画面は /login へ倒れる', async () => {
        await expect(async () => {
          expect(
            (await subject.request.get('/bff/auth/me')).status(),
            '/bff/auth/me（無効化の後）',
          ).toBe(401);
        }).toPass(REVOKE_WAIT);
        // 業務の API（読み取り）にも失効が効く（`/bff/auth/me` 以外の、保護された口）。
        expect(
          (await subject.request.get(BUSINESS_API)).status(),
          `GET ${BUSINESS_API}（無効化の後）`,
        ).toBe(401);
        await subject.goto('/my/notes');
        await expect(subject).toHaveURL(/\/login(\?|$)/);
      });

      await test.step('M-109: 再有効化すると「有効」に戻り、失効したセッションは戻らない', async () => {
        const enabled = bffResponse(page, 'POST', `/bff/admin/users/${before.id}/enable`);
        await page.getByRole('button', { name: T.enable, exact: true }).click();
        expect((await enabled).status(), 'POST /bff/admin/users/{id}/enable').toBe(200);
        await expect(row.getByText(T.enabledBadge, { exact: true })).toBeVisible();
        expect((await subjectUser(page.request)).enabled).toBe(true);
        expect(
          (await subject.request.get('/bff/auth/me')).status(),
          '/bff/auth/me（再有効化の後）',
        ).toBe(401);
        expect(
          (await subject.request.get(BUSINESS_API)).status(),
          `GET ${BUSINESS_API}（再有効化の後も旧セッションのまま）`,
        ).toBe(401);
      });
    } finally {
      await subjectContext.close();
    }
  },
);
