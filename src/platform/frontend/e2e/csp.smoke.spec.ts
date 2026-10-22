import { readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';
import { test, expect } from './support/cspGuard';

// NFR / IADR-0579（Refs GHSA-5h4q-4m2j-rjhm）: 配信の CSP。
//
// 他の spec は `support/cspGuard` の下で「違反が 0 件」を確かめる。それが空振りでないことを、
// ここで対にして固定する —— ①ヘッダが本番（Caddyfile）と同じ値で付いている、②その下では
// インラインのスクリプトと外部への取得が実際に拒まれ、ガードがそれを観測できる。

function caddyCsp(): string {
  const caddyfile = readFileSync(fileURLToPath(new URL('../Caddyfile', import.meta.url)), 'utf8');
  const m = /^\s*Content-Security-Policy\s+"([^"]+)"\s*$/m.exec(caddyfile);
  if (!m) throw new Error('Caddyfile に Content-Security-Policy の行が見つからない');
  return m[1];
}

test('serves the same Content-Security-Policy as the production Caddyfile', async ({ page }) => {
  const res = await page.goto('/');
  expect(res, '/ の応答がありません').not.toBeNull();
  const csp = res!.headers()['content-security-policy'];
  expect(csp).toBe(caddyCsp());
  expect(csp).toContain("script-src 'self'");
  expect(csp).toContain("object-src 'none'");
  expect(csp).toContain("frame-ancestors 'none'");
  // 陽性対照: CSP の下でも画面は立ち上がる。
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test.describe('negative control', () => {
  test.use({ allowCspViolations: true });

  test('blocks inline script and external fetches under the policy', async ({
    page,
    cspViolations,
  }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();

    // e2e の tsconfig は DOM の型を持たないため、ページ側のコードは文字列で渡す。
    const result = await page.evaluate<{
      inlineRan: boolean;
      fetchBlocked: boolean;
    }>(`(async () => {
      const s = document.createElement('script');
      s.textContent = 'window.__inlineRan = true;';
      document.body.appendChild(s);
      const img = document.createElement('img');
      img.src = 'https://example.invalid/x.png';
      document.body.appendChild(img);
      let fetchBlocked = false;
      try { await fetch('https://example.invalid/x'); } catch { fetchBlocked = true; }
      return { inlineRan: window.__inlineRan === true, fetchBlocked };
    })()`);

    expect(result.inlineRan).toBe(false);
    expect(result.fetchBlocked).toBe(true);
    // ガードの観測点が実際に違反を拾う。
    await expect
      .poll(() => cspViolations.map((v) => v.split(' ')[0]))
      .toEqual(expect.arrayContaining(['script-src-elem', 'img-src', 'connect-src']));
  });

  // ガードは文書をまたぐ遷移の前に起きた違反を失わない（ページ内に溜めない）。
  test('keeps violations that happened before a cross-document navigation', async ({
    page,
    cspViolations,
  }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
    await page.evaluate(`(() => {
      const s = document.createElement('script');
      s.textContent = 'void 0;';
      document.body.appendChild(s);
    })()`);
    // ページ内のイベント（securitypolicyviolation）の経路で届いたことを待つ（コンソールの経路は元から遷移に強い）。
    await expect.poll(() => cspViolations.some((v) => v.startsWith('script-src-elem'))).toBe(true);
    const before = [...cspViolations];

    // 文書をまたぐ遷移（全読み込み）。遷移の後も、遷移の前の違反が残っている。
    await page.goto('/login');
    await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
    expect(cspViolations).toEqual(expect.arrayContaining(before));
  });
});
