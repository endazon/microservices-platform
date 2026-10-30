// NFR-10 / ADR-0026 / ADR-0032 / IADR-0624 / IADR-0626（#2025・#2027）: live 層の**実ログインの部品**。
// `auth.setup.ts`（6 役割の storageState を作る）と、ログインそのものを試す spec（`auth-errors.ro.spec.ts`）が同じものを使う。
//
// - 🔴 秘密の値（パスワード・OTP）は `fillSecret` で入れる。`locator.fill(value)` は値を step の題に載せ、html レポートに残す。
// - 🔴 ログインを行う project / spec は trace・スクリーンショット・動画を切る（trace はフォームの POST 本文＝パスワードと、
//   応答の `Set-Cookie` を記録する）。失敗した段は `stage` が文字で attach する（URL は origin と path だけ）。
import { expect, type Locator, type Page, type TestInfo } from '@playwright/test';

/**
 * 🔴 秘密の値を入力欄へ入れる。`locator.fill(value)` は**値を step の題に載せ、html レポートに残す**（実測: 6 名分の
 * パスワードがレポートに出た）。`evaluate` の引数は題に載らないので、値をページの中で入れて input / change を起こす。
 */
export async function fillSecret(locator: Locator, value: string): Promise<void> {
  await expect(locator).toBeEditable();
  await locator.evaluate((el, v) => {
    const input = el as unknown as { value: string; dispatchEvent: (e: unknown) => boolean };
    input.value = v;
    const EventCtor = (globalThis as unknown as { Event: new (t: string, o: object) => unknown })
      .Event;
    input.dispatchEvent(new EventCtor('input', { bubbles: true }));
    input.dispatchEvent(new EventCtor('change', { bubbles: true }));
  }, value);
}

/** URL の origin（スキーム・ホスト・ポート）と path だけ。query と fragment（state・code・sid が載る）は出さない。 */
export function originAndPath(raw: string): string {
  try {
    const u = new URL(raw);
    return `${u.origin}${u.pathname}`;
  } catch {
    return '（URL ではない）';
  }
}

/**
 * Keycloak の画面に出ている表示文言（エラー・案内）と、OTP の欄・フォームの有無。表示文言は秘密ではない（入力値は読まない）。
 * Keycloak の画面のときだけ読む（アプリの画面の文言は載せない）。
 */
async function keycloakScreen(page: Page, kcUrl: string): Promise<string[]> {
  let origin = '';
  try {
    origin = new URL(page.url()).origin;
  } catch {
    return [];
  }
  if (origin !== kcUrl) return [];
  const texts = await page
    .locator(
      '#input-error, [id^="input-error"], .alert-error, .pf-m-danger, .kc-feedback-text, #kc-feedback-text',
    )
    .allInnerTexts()
    .catch(() => [] as string[]);
  const shown = [...new Set(texts.map((t) => t.replace(/\s+/g, ' ').trim()).filter(Boolean))].map(
    (t) => t.slice(0, 200),
  );
  const forms = await page
    .locator('form[id]')
    .evaluateAll((els) => els.map((e) => String((e as unknown as { id: string }).id)))
    .catch(() => [] as string[]);
  const otp = await page
    .locator('#otp')
    .count()
    .catch(() => -1);
  return [
    `keycloak messages: ${shown.length > 0 ? shown.join(' | ') : '（なし）'}`,
    `keycloak forms: ${forms.join(', ') || '（なし）'}`,
    `#otp field: ${otp > 0 ? 'ある' : otp === 0 ? '無い' : '（取れない）'}`,
  ];
}

/** 失敗した段を残す。URL は origin と path だけ。値は残さない。`notes` は段が集めた診断（同じく値を含めない）。 */
export async function stage<T>(
  testInfo: TestInfo,
  page: Page,
  kcUrl: string,
  name: string,
  run: (notes: string[]) => Promise<T>,
): Promise<T> {
  const notes: string[] = [];
  try {
    return await run(notes);
  } catch (e) {
    await testInfo.attach('failed-stage', {
      body:
        [
          `stage: ${name}`,
          `url: ${originAndPath(page.url())}`,
          `title: ${await page.title().catch(() => '（取れない）')}`,
          `expected keycloak origin: ${kcUrl}`,
          ...(await keycloakScreen(page, kcUrl)),
          ...notes,
        ].join('\n') + '\n',
      contentType: 'text/plain',
    });
    throw e;
  }
}

/** Keycloak のログイン画面（`#kc-form-login`）で ID とパスワードを送る。パスワードは `fillSecret` で入れる。 */
export async function submitPassword(page: Page, username: string, password: string) {
  await expect(page.locator('#kc-form-login')).toBeVisible();
  await page.locator('#username').fill(username);
  await fillSecret(page.locator('#password'), password);
  await page.locator('#kc-login').click();
}

/** Keycloak の OTP 画面（`#otp`）で 6 桁を送る。 */
export async function submitOtp(page: Page, code: string) {
  await expect(page.locator('#otp')).toBeVisible();
  await fillSecret(page.locator('#otp'), code);
  await page.locator('#kc-login').click();
}
