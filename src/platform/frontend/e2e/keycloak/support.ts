import { readFileSync } from 'node:fs';
import { createHmac, randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import AxeBuilder from '@axe-core/playwright';
import {
  expect,
  request,
  test as base,
  type APIRequestContext,
  type Page,
  type TestInfo,
} from '@playwright/test';
import { THEMES, WCAG_22_AA_TAGS } from '../support/axe';
import { KNOWN_KEYCLOAK_AXE_VIOLATIONS } from './axeKnownViolations';

// NFR-30 / ADR-0026 / ADR-0031 (#2191 / IADR-0619): **Keycloak のテーマの画面を axe で走査する土台。**
//
// SPA の土台（`../support/axe.ts`。IADR-0614）と**タグ・テーマ・台帳の作法を揃える**。違うのは 2 点だけ:
//
// ■ テーマの当て方。SPA は `<html data-theme>` を書き換えるが、Keycloak のテーマは OS 設定
//   （`prefers-color-scheme`）だけで明暗が決まる（IADR-0532 決定 3。切替ボタンを持たない）。
//   そこで明暗は **Playwright の project（`playwright.keycloak.config.ts` の `colorScheme`）**で与え、
//   ページを開く前から決めておく。描いた後に `emulateMedia` で切り替えると、親（keycloak.v2 / v3）の
//   スクリプトが付ける `pf-v5-theme-dark` が追随するかが版に依存し、どちらを測ったか決め打ちできない。
// ■ 陽性対照に「**本当に platform テーマのその明暗で描かれた**」ことを足す。テーマが解決できないと
//   Keycloak は既定のテーマへ**黙って**落ちる（IADR-0532 の実測）。既定のテーマも axe はおおむね通るので、
//   違反 0 件だけでは「自分たちの CSS を測った」ことにならない。`tokens.css`（生成物）の `--color-bg` の値が
//   `:root` に効いていることを毎回確かめる。

const TOKENS_CSS = fileURLToPath(
  new URL(
    '../../../../../deploy/keycloak/themes/platform/login/resources/css/tokens.css',
    import.meta.url,
  ),
);

type Theme = (typeof THEMES)[number];

/** テーマのラベル補修スクリプト（`login/resources/js/platform-a11y.js`）が補えなかったときに出す console.error の接頭辞。 */
const REPAIR_FAILURE_PREFIX = '[platform-a11y]';

/**
 * spec はここから `test` / `expect` を引く。**ラベルの補修が黙って何もしない状態を落とす**自動 fixture を持つ。
 *
 * 補修スクリプトは、壊れたラベル（`for` の指す要素が無い）を見つけたのに付け替え先を決められないとき
 * （PF5 のフォームグループのクラスが版上げで変わった・入力欄が 0 個や 2 個以上）に `console.error` を出す。
 * axe の `label` 違反でも捕まるが、原因（補修が効かなくなった）をそのまま出すため、ここでも落とす。
 */
export const test = base.extend<{ labelRepairGuard: void }>({
  labelRepairGuard: [
    // 第 2 引数は Playwright の慣用では `use` だが、react-hooks の規則が React の `use` と取り違えるため名前を変える。
    async ({ page }, provide) => {
      const failures: string[] = [];
      page.on('console', (message) => {
        if (message.text().startsWith(REPAIR_FAILURE_PREFIX)) failures.push(message.text());
      });
      await provide();
      expect(failures, 'ラベルの補修（platform-a11y.js）が付け替え先を決められなかった').toEqual(
        [],
      );
    },
    { auto: true },
  ],
});

export { expect };

/** `tokens.css` の `--color-bg` を、既定（ダーク）とライトのメディアクエリの順に読む。 */
function expectedBackground(theme: Theme): string {
  const values = [...readFileSync(TOKENS_CSS, 'utf8').matchAll(/--color-bg:\s*([^;]+);/g)].map(
    (m) => m[1].trim(),
  );
  // 生成器（scripts/gen-keycloak-theme-tokens.js）は既定の :root（ダーク）→ ライトの順に書く。2 つでなければ形が変わった。
  expect(values, 'tokens.css の --color-bg（ダーク・ライトの 2 つ）').toHaveLength(2);
  return theme === 'dark' ? values[0] : values[1];
}

/** いま描かれている面の明暗（`prefers-color-scheme`）。project の `colorScheme` が効いていることも兼ねる。 */
async function currentTheme(page: Page): Promise<Theme> {
  // e2e の tsconfig は DOM の型を持たない（`../support/axe.ts` の注記）ため、ページ側の式は文字列で渡す。
  const dark = await page.evaluate<boolean>("matchMedia('(prefers-color-scheme: dark)').matches");
  return dark ? 'dark' : 'light';
}

/**
 * いま描かれている Keycloak の面を走査し、違反が 0 件か台帳の指紋と一致することを確かめる。
 * 明暗は project が決める（1 回の呼び出しで 1 テーマ）。両テーマは 2 つの project が 1 回ずつ通る。
 */
export async function expectKeycloakAxeClean(
  page: Page,
  surface: string,
  testInfo: TestInfo,
): Promise<void> {
  // 🔴 遷移を止めてから測る（SPA と同じ理由。補間の途中の色を axe が読むとコントラスト比が低く出る）。
  await page.addStyleTag({
    content: '*, *::before, *::after { transition: none !important; animation: none !important; }',
  });

  const theme = await currentTheme(page);
  const key = `${surface} / ${theme}`;

  // ★ 陽性対照: platform テーマ（生成したトークン）が、その明暗で効いていること。
  const background = await page.evaluate<string>(
    "getComputedStyle(document.documentElement).getPropertyValue('--color-bg').trim()",
  );
  expect(background, `${key} の --color-bg（platform テーマが効いていない）`).toBe(
    expectedBackground(theme),
  );

  const results = await new AxeBuilder({ page }).withTags([...WCAG_22_AA_TAGS]).analyze();
  await testInfo.attach(`axe ${surface} ${theme}`, {
    body: JSON.stringify(results.violations, null, 2),
    contentType: 'application/json',
  });

  const found = results.violations
    .flatMap((v) => v.nodes.map((n) => `${v.id} ${n.target.join(' ')}`))
    .sort();
  const known = (KNOWN_KEYCLOAK_AXE_VIOLATIONS[key] ?? []).map((k) => k.fingerprint).sort();
  // `expect.soft`: 1 本の test が複数の面を歩くため、最初の面の違反で止めず、後続の面の違反もまとめて出す
  // （test は失敗のまま終わる）。
  expect.soft(found, key).toEqual(known);

  // ★ 陽性対照: NFR-30 のタグで走ったこと・axe が実際に走ったこと（SPA の土台と同じ）。
  expect(results.toolOptions.runOnly, `${key} の走査タグ`).toEqual({
    type: 'tag',
    values: [...WCAG_22_AA_TAGS],
  });
  expect(results.passes.length, `${key} で axe の合格判定が 0 件`).toBeGreaterThan(0);
}

/**
 * 台帳のキーのうち、spec のどの `expectKeycloakAxeClean(page, '<面>', …)` にも当たらないものを返す
 * （SPA の `deadKnownViolationKeys` と同じ理由。集合は実行時ではなく本文から作る）。
 */
export function deadKeycloakKnownViolationKeys(specSource: string): string[] {
  const surfaces = [...specSource.matchAll(/expectKeycloakAxeClean\(\s*page,\s*'([^']+)'/g)].map(
    (m) => m[1],
  );
  const scanned = new Set(surfaces.flatMap((surface) => THEMES.map((t) => `${surface} / ${t}`)));
  return Object.keys(KNOWN_KEYCLOAK_AXE_VIOLATIONS).filter((key) => !scanned.has(key));
}

// ───────────────────────── 走査用の利用者（管理 API） ─────────────────────────
//
// 🔴 **使い捨ての Keycloak でだけ動かす。** 接続先は `E2E_KC_URL`（IADR-0569 決定 4 で予約した名前）、
// 管理者は `E2E_KC_ADMIN_USER` / `E2E_KC_ADMIN_PASSWORD`。CI はジョブの中で起こしたコンテナを指し、
// 稼働の Keycloak（k3s）は指さない（利用者を作って消すため。L2 の「書き込み禁止」に当たる）。

export const REALM = 'platform';

function env(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`${name} が未設定（playwright.keycloak.config.ts の注記）`);
  return value;
}

// 管理 API は Playwright の `APIRequestContext` で呼ぶ（素の `fetch` は ESLint が禁じる。SPA の通信を 1 箇所へ収束させる
// 規則で、e2e も対象に入る）。
async function adminApi(): Promise<APIRequestContext> {
  const api = await request.newContext({ baseURL: env('E2E_KC_URL') });
  const response = await api.post('/realms/master/protocol/openid-connect/token', {
    form: {
      grant_type: 'password',
      client_id: 'admin-cli',
      username: env('E2E_KC_ADMIN_USER'),
      password: env('E2E_KC_ADMIN_PASSWORD'),
    },
  });
  expect(response.status(), '管理者トークン').toBe(200);
  const { access_token: token } = (await response.json()) as { access_token: string };
  await api.dispose();
  return request.newContext({
    baseURL: env('E2E_KC_URL'),
    extraHTTPHeaders: { authorization: `Bearer ${token}` },
  });
}

/** 走査用の利用者を作る。パスワードは realm のポリシー（12 文字以上・3 種以上）を満たす乱数。 */
export async function createUser(
  requiredActions: string[],
): Promise<{ username: string; password: string; remove: () => Promise<void> }> {
  const api = await adminApi();
  const suffix = randomBytes(4).toString('hex');
  const username = `e2e-a11y-${suffix}`;
  const password = `A11y-${randomBytes(9).toString('base64url')}9a`;
  const created = await api.post(`/admin/realms/${REALM}/users`, {
    data: {
      username,
      enabled: true,
      email: `${username}@example.invalid`,
      emailVerified: true,
      firstName: '走査',
      lastName: '利用者',
      requiredActions,
      credentials: [{ type: 'password', value: password, temporary: false }],
    },
  });
  expect(created.status(), `利用者 ${username} の作成`).toBe(201);
  const location = created.headers()['location'];
  expect(location, '作成した利用者の Location').toBeTruthy();
  await api.dispose();
  return {
    username,
    password,
    remove: async () => {
      // 管理トークンは短命（既定 60 秒）なので、消すときに取り直す。
      const admin = await adminApi();
      const removed = await admin.delete(location);
      // 🔴 消えたことを確かめる。黙って失敗すると、走査用の利用者が使い捨ての Keycloak に残り続ける
      // （同じジョブの後続の test が別の利用者名を使うので走査は通り、残ったことに誰も気付かない）。
      expect(removed.status(), `利用者 ${username} の削除`).toBe(204);
      await admin.dispose();
    },
  };
}

/**
 * TOTP（RFC 6238。realm の otpPolicy = HmacSHA1・6 桁・30 秒）。鍵は CONFIGURE_TOTP の画面の
 * hidden 入力 `totpSecret` の文字列そのもの（Keycloak は表示用にだけ Base32 にする）。
 */
export function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / 30)));
  const digest = createHmac('sha1', Buffer.from(secret, 'utf8')).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  const code = (digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return code.toString().padStart(6, '0');
}
