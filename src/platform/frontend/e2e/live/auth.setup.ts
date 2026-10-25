// NFR-10 / ADR-0026 / ADR-0032 / IADR-0569 / IADR-0570 / IADR-0624（#2025）: live 層の**ログイン**。
// 試験アカウント 6 役割で 1 回ずつ、実 Keycloak（パスワード → TOTP）→ 実 BFF の callback を通し、BFF セッション cookie
// （`__Host-msp-session`）を storageState に保存する。spec は `test.use({ storageState: stateFile(authDir, role) })` で使う。
//
// - 🔴 SPA はトークンを扱わない（ADR-0032）。保存するのは BFF の cookie だけで、トークンは BFF の中にある。
// - 🔴 state は `authDir`（レポートの外・gitignore）にだけ書く。この project は trace・スクリーンショット・動画を切っている
//   （trace は `fill` の値＝パスワードと、応答の `Set-Cookie` を記録するため。`playwright.live.config.ts`）。
// - brute force 保護（5 回失敗で 900 秒）があるため retries は 0。失敗した段を attach して fail にする（skip にしない）。
import { chmodSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { expect, test as setup, type Page, type TestInfo } from '@playwright/test';
import { credentialsOf, parseLiveEnv, ROLES, stateFile, type Role } from './support/env';
import { freshOtp } from './support/totp';
import { DEFAULT_AUTH_DIR } from './support/paths';
import { originAndPath, stage as stageAt, submitOtp, submitPassword } from './support/login';

const cfg = parseLiveEnv(process.env, DEFAULT_AUTH_DIR);

/** 役割を見分けるロール（realm `platform`）。IADR-0570 の宣言と同じ。 */
const ROLE_MARKERS = ['platform-admin', 'platform-operator', 'trading-owner'] as const;
const EXPECTED_ROLES: Record<Role, string[]> = {
  user: [],
  operator: ['platform-operator'],
  admin: ['platform-admin', 'platform-operator'],
  noaccess: [],
  revocable: [],
  'trading-owner': ['trading-owner'],
};

/** 失敗した段を文字で残す（部品は `support/login.ts`。IADR-0626 で ro の spec と共有した）。 */
const stage = <T>(
  testInfo: TestInfo,
  page: Page,
  name: string,
  run: (notes: string[]) => Promise<T>,
): Promise<T> => stageAt(testInfo, page, cfg.kcUrl, name, run);

async function logIn(page: Page, testInfo: TestInfo, role: Role): Promise<void> {
  // 欠けていれば Keycloak へ送る前に例外（空の値を誤入力として数えさせない）。
  const { password, totpSecret: secret } = credentialsOf(role, process.env);
  const username = `e2e-${role}`;

  await stage(testInfo, page, 'ログイン画面', async (notes) => {
    // 診断: BFF のログインの入口が返す状態と Location の origin（だけ）を残す。ブラウザの遷移とは別の要求で、ログインの試行には数えない。
    const probe = await page.request.get('/bff/auth/login?returnUrl=%2F', {
      maxRedirects: 0,
      failOnStatusCode: false,
    });
    notes.push(
      `bff /bff/auth/login: ${probe.status()} → Location ${originAndPath(probe.headers()['location'] ?? '')}`,
    );
    const landed = await page.goto('/bff/auth/login?returnUrl=%2F');
    notes.push(
      `browser landed: ${landed ? landed.status() : '応答なし'} ${originAndPath(page.url())}`,
    );
    await page.waitForURL((url) => url.origin === cfg.kcUrl);
    await submitPassword(page, username, password);
  });
  await stage(testInfo, page, 'OTP 画面', async () => {
    await submitOtp(page, await freshOtp(secret));
  });
  await stage(testInfo, page, 'コールバック', async () => {
    // Keycloak が必須アクション（OTP の登録・パスワードの更新など）の画面へ送ったら、待ち続けずにそう言って落とす
    // （#2026 の初回の実行で、宣言に requiredActions が無い試験アカウントに既定の CONFIGURE_TOTP が付き、ここで 60 秒待った）。
    await page.waitForURL(
      (url) =>
        (url.origin === cfg.edgeUrl && !url.pathname.startsWith('/bff/')) ||
        (url.origin === cfg.kcUrl && url.pathname.includes('/login-actions/required-action')),
    );
    if (new URL(page.url()).pathname.includes('/login-actions/required-action')) {
      const forms = await page
        .locator('form[id]')
        .evaluateAll((els) => els.map((e) => String((e as unknown as { id: string }).id)));
      throw new Error(
        `Keycloak が必須アクションを求めている（フォーム: ${forms.join(', ') || '不明'}）。試験アカウントに requiredActions が残っている` +
          '（realm の宣言は requiredActions: [] を明示し、reconcile-realm の Job は作成の後に PUT で当て直す。既存の利用者は管理コンソールで外すか、消して作り直す）',
      );
    }
    const me = await page.request.get('/bff/auth/me');
    expect(me.status(), '/bff/auth/me（ログインの後）').toBe(200);
    // 身元の口は名前・subject・ロールを返す（トークンは返さない）。ロールで「その役割として入った」ことを確かめる。
    const body = (await me.json()) as { subject?: string; roles?: string[] };
    expect(body.subject, 'subject').toBeTruthy();
    const marked = (body.roles ?? [])
      .filter((r) => (ROLE_MARKERS as readonly string[]).includes(r))
      .sort();
    expect(marked, `${username} のロール（${ROLE_MARKERS.join(' / ')} のうち）`).toEqual(
      EXPECTED_ROLES[role],
    );
  });
}

for (const role of ROLES) {
  setup(`e2e-${role} でログインし storageState を保存する`, async ({ page }, testInfo) => {
    await logIn(page, testInfo, role);
    const file = stateFile(cfg.authDir, role);
    mkdirSync(dirname(file), { recursive: true });
    const state = await page.context().storageState({ path: file });
    // cookie を持つファイルは本人だけが読める権限にする（#2231。残す opt-out の state も同じ権限で残る）。
    chmodSync(file, 0o600);
    // BFF のオリジンのセッション cookie が保存されたこと（値は見ない・出さない）。
    const host = new URL(cfg.edgeUrl).hostname;
    expect(
      state.cookies.filter((c) => c.domain.replace(/^\./, '') === host).length,
      'BFF の cookie',
    ).toBeGreaterThan(0);
  });
}
