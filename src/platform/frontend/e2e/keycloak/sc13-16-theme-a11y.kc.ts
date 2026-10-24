import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test, expect, type Page } from '@playwright/test';
import {
  REALM,
  createUser,
  deadKeycloakKnownViolationKeys,
  expectKeycloakAxeClean,
  totp,
} from './support';

// NFR-30（WCAG 2.2 レベル AA）/ SC-13〜16 / ADR-0026 (#2191 / IADR-0619): **Keycloak のテーマが描く画面**を
// axe（NFR-30 の 5 タグ＋best-practice）で、ライト・ダークの両方について走査する。
// IADR-0614 決定 3 が L1 の対象外とした残余（SC-13 の Keycloak 側・SC-14〜16）を受ける。
//
// 面の選び方: 計画 05_screens の各画面の「主要素」が描かれる状態を 1 つずつ（入力・エラー・完了の表示を含む）。
// - SC-13: ログイン・認証エラー（固定文言）
// - SC-14: 初回登録（QR コード／手入力キー）・ワンタイムコード・コードの誤り
// - SC-15: 申請・申請後（「メールを送信しました」）・新パスワードの設定
// - SC-16: アカウントコンソールの個人情報・サインイン（パスワード・OTP デバイス）・デバイスの活動（セッション）
// - 共通: Keycloak のエラー画面（親テンプレート error.ftl。不正なクライアントで描く）
//
// メール（SC-15 のリセットメール。email テーマ）は走査しない —— ブラウザの画面ではなく、外枠の色は
// `gen-keycloak-theme-tokens.js --check` がライトの意味トークンの値に限っている（IADR-0532 決定 2）。

const ACCOUNT = `/realms/${REALM}/account/`;

async function openLogin(page: Page): Promise<void> {
  // アカウントコンソールを開くと未認証のためログイン画面（SC-13）へ送られる。SPA のクライアント（platform-spa）の
  // 経路と同じテンプレート（login.ftl）・同じテーマである。
  await page.goto(ACCOUNT);
  await expect(page.locator('#kc-form-login')).toBeVisible();
}

async function signIn(page: Page, username: string, password: string): Promise<void> {
  await page.locator('#username').fill(username);
  await page.locator('#password').fill(password);
  await page.locator('#kc-login').click();
}

test('SC-13 ログイン画面と認証エラー', async ({ page }, testInfo) => {
  await openLogin(page);
  await expectKeycloakAxeClean(page, 'SC-13 ログイン', testInfo);

  // 存在しない利用者名で失敗させる（実在の利用者の総当たり検知を踏まない）。文言は固定（SC-13）。
  await signIn(page, `e2e-a11y-absent-${Date.now()}`, 'wrong-password-1A');
  await expect(page.getByText('社員ID またはパスワードが正しくありません')).toBeVisible();
  await expectKeycloakAxeClean(page, 'SC-13 認証エラー', testInfo);
});

test('SC-15 パスワードリセットの申請と申請後', async ({ page }, testInfo) => {
  await openLogin(page);
  await page.getByRole('link', { name: 'パスワードを忘れた' }).click();
  await expect(page.locator('#kc-reset-password-form')).toBeVisible();
  await expectKeycloakAxeClean(page, 'SC-15 申請', testInfo);

  // 存在しない利用者名で申請する（メールは作られない。応答は実在と区別できない＝SC-15 の存在秘匿）。
  await page.locator('#username').fill(`e2e-a11y-absent-${Date.now()}`);
  await page.getByRole('button', { name: '送信' }).click();
  await expect(page.getByText('メールを送信しました')).toBeVisible();
  await expectKeycloakAxeClean(page, 'SC-15 申請後', testInfo);
});

test('SC-14 初回登録・SC-15 新パスワード・SC-16 アカウントコンソール・SC-14 ワンタイムコード', async ({
  page,
}, testInfo) => {
  // 初回ログインで OTP の登録（CONFIGURE_TOTP）→ パスワードの更新（UPDATE_PASSWORD。メールのリンクから開く
  // 新パスワードの設定と同じテンプレート login-update-password.ftl）の順に必須アクションを踏ませる。
  const user = await createUser(['CONFIGURE_TOTP', 'UPDATE_PASSWORD']);
  try {
    await openLogin(page);
    await signIn(page, user.username, user.password);

    // SC-14 初回登録（QR コード）
    await expect(page.locator('#kc-totp-settings-form')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-14 初回登録', testInfo);

    // SC-14 初回登録（手入力キー）
    await page.locator('#mode-manual').click();
    await expect(page.locator('#kc-totp-secret-key')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-14 初回登録（手入力キー）', testInfo);

    const secret = await page.locator('#totpSecret').inputValue();
    await page.locator('#totp').fill(totp(secret));
    await page.locator('#userLabel').fill('走査用');
    await page.locator('#kc-totp-settings-form [type=submit]').first().click();

    // SC-15 新パスワードの設定
    await expect(page.locator('#password-new')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-15 新パスワードの設定', testInfo);
    const newPassword = `${user.password}-N`;
    await page.locator('#password-new').fill(newPassword);
    await page.locator('#password-confirm').fill(newPassword);
    await page.locator('#kc-passwd-update-form [type=submit]').first().click();

    // SC-16 アカウントコンソール（keycloak.v3。React で描かれるので、主要素が出るまで待つ）
    await page.waitForURL((url) => url.pathname.startsWith(ACCOUNT));
    await expect(page.locator('#username')).toHaveValue(user.username);
    await expectKeycloakAxeClean(page, 'SC-16 個人情報', testInfo);

    await page.goto(`${ACCOUNT}account-security/signing-in`);
    await expect(page.getByText('走査用')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-16 サインイン', testInfo);

    await page.goto(`${ACCOUNT}account-security/device-activity`);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.getByText('Chrome').first()).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-16 デバイスの活動', testInfo);

    // SC-14 ワンタイムコード（登録済みの利用者の 2 度目のログイン）
    await page.context().clearCookies();
    await openLogin(page);
    await signIn(page, user.username, newPassword);
    await expect(page.locator('#otp')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-14 ワンタイムコード', testInfo);

    // 誤ったコード（現在のコードと必ず違う値にする）
    const current = totp(secret);
    await page.locator('#otp').fill(current === '000000' ? '111111' : '000000');
    await page.locator('#kc-login').click();
    await expect(page.locator('#input-error-otp')).toBeVisible();
    await expectKeycloakAxeClean(page, 'SC-14 ワンタイムコードの誤り', testInfo);
  } finally {
    await user.remove();
  }
});

test('Keycloak のエラー画面', async ({ page }, testInfo) => {
  // 実在しないクライアントで認可エンドポイントを開くと、親テンプレートの error.ftl が描かれる。
  const params = new URLSearchParams({
    client_id: 'e2e-a11y-absent-client',
    response_type: 'code',
    redirect_uri: 'http://localhost/',
  });
  await page.goto(`/realms/${REALM}/protocol/openid-connect/auth?${params}`);
  await expect(page.locator('#kc-error-message')).toBeVisible();
  await expectKeycloakAxeClean(page, 'Keycloak エラー画面', testInfo);
});

// ───────────────────────────── 台帳の整合 ─────────────────────────────

test('既知の違反の台帳に、どの走査にも当たらないキーが無い', () => {
  const source = readFileSync(fileURLToPath(import.meta.url), 'utf8');
  // ★ 陽性対照: 面名を本文から拾えていること。閾値は現在の面の数（13）である。**面を足したら上げる。**
  expect(source.match(/expectKeycloakAxeClean\(page, '/g)?.length ?? 0).toBeGreaterThanOrEqual(13);
  expect(deadKeycloakKnownViolationKeys(source)).toEqual([]);
});
