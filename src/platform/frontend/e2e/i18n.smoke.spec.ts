import { test, expect } from './support/cspGuard';
import type { Page } from '@playwright/test';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// M-020（#2023 / E-18）, NFR, IADR-0125 決定 7・8, IADR-0569: **en-US でもブランド表示名は翻訳されず、ナビは英語になる。**
//
// 期待値は計画 05_screens §共通シェル から起こした。
// - ブランド表示名は「汎用プラットフォーム」で統一し、**ロケールによっても差し替えない**
//   （固有名詞として扱い、翻訳カタログの対象としない。2026-08-04 確定）。
// - 表示言語はブラウザの言語設定から決まる（切替 UI は持たない）。
//
// 本 spec は project `smoke-en-us`（`locale: 'en-US'`）だけで走る（playwright.config.ts）。
// 🔴 **陽性対照に、同じ画面を ja-JP の文脈でも開く。** en-US の文脈だけを見ると、「ナビが英語」が
// 「ロケールが効いた」のか「もともと英語だった」のか区別できない。ja-JP で日本語、en-US で英語に
// 描き分けられ、ブランド表示名だけが両方で同じであることを対で見る。
//
// 英語の文言そのもの（"Knowledge graph" など）は翻訳カタログが持つ実装の値であり、計画は定めない。
// ここでは「日本語の文字を含まない」ことで英語であることを判定し、カタログの訳語を写さない。

const BRAND = '汎用プラットフォーム';
const JAPANESE = /[぀-ヿ㐀-鿿]/;

/** 左ナビのリンク名（アクセシビリティツリーの順）。ナビは名前で引かず、唯一の navigation を使う。 */
async function navLinkNames(page: Page): Promise<string[]> {
  const nav = page.getByRole('navigation');
  await expect(nav.getByRole('link').filter({ visible: true })).not.toHaveCount(0);
  return (await nav.getByRole('link').allTextContents()).map((n) => n.trim());
}

/** 共通シェルを開く。未知のパスでも共通シェル（ヘッダ・左ナビ）は描かれ、画面固有の応答を要らない（NotFound を描く）。 */
async function openShell(page: Page) {
  const traffic = await installBffSession(page, { user: sessionUser([]) });
  await page.goto('/my/notes-does-not-exist');
  return traffic;
}

test('M-020: en-US ではナビが英語になり、ブランド表示名は「汎用プラットフォーム」のまま（ja-JP と対で見る）', async ({
  page,
  browser,
  baseURL,
}) => {
  // ★ en-US（この project の文脈）。
  const traffic = await openShell(page);
  await expect(page.locator('header').getByText(BRAND, { exact: true })).toBeVisible();
  const enNav = await navLinkNames(page);
  expect(enNav.length).toBeGreaterThan(0);
  expect(enNav.filter((n) => JAPANESE.test(n))).toEqual([]);
  // ブランド表示名はどこにも訳されて現れない（英語の別名を出さない）。
  await expect(page.locator('header')).not.toContainText(/General[- ]purpose platform/i);
  expectBffTrafficIsComplete(traffic);

  // ★ 陽性対照: 同じ画面を ja-JP の文脈で開くと、ナビは日本語で、ブランド表示名は同じ。
  const jaContext = await browser.newContext({ locale: 'ja-JP', baseURL });
  try {
    const jaPage = await jaContext.newPage();
    const jaTraffic = await openShell(jaPage);
    await expect(jaPage.locator('header').getByText(BRAND, { exact: true })).toBeVisible();
    const jaNav = await navLinkNames(jaPage);
    expect(jaNav).toHaveLength(enNav.length);
    // ナビの項目は同じ数・同じ並びで、各項目は「ja では日本語で、en と違う」か「両方で同じ固有名詞
    // （日本語の文字を含まない。例 Wiki）」のどちらかである。訳された項目が 1 つも無ければ赤にする。
    const translated = jaNav.filter((n, i) => JAPANESE.test(n) && n !== enNav[i]);
    const sameProperNoun = jaNav.filter((n, i) => n === enNav[i] && !JAPANESE.test(n));
    expect(translated.length + sameProperNoun.length).toBe(jaNav.length);
    expect(translated.length).toBeGreaterThan(sameProperNoun.length);
    expectBffTrafficIsComplete(jaTraffic);
  } finally {
    await jaContext.close();
  }
});

test('M-020: 未認証のログイン画面でも、en-US でブランド表示名は訳されない', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: BRAND, level: 1 })).toBeVisible();
  // 陽性対照: ログインの操作の文言は英語になっている（ロケールが効いている）。
  const button = page.getByRole('button', { name: /Keycloak/ });
  await expect(button).toBeVisible();
  expect(JAPANESE.test((await button.textContent()) ?? '')).toBe(false);
});
