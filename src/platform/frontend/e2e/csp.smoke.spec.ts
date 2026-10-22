import { readFileSync } from 'node:fs';
import { fileURLToPath, URL } from 'node:url';
import { test, expect } from './support/cspGuard';

// NFR / IADR-0578（Refs GHSA-5h4q-4m2j-rjhm）: 配信の CSP。
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

  test('blocks inline script and external fetches under the policy', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();

    // e2e の tsconfig は DOM の型を持たないため、ページ側のコードは文字列で渡す。
    const result = await page.evaluate<{
      inlineRan: boolean;
      fetchBlocked: boolean;
      directives: string[];
    }>(`(async () => {
      const violations = window.__cspViolations ?? [];
      const before = violations.length;
      const s = document.createElement('script');
      s.textContent = 'window.__inlineRan = true;';
      document.body.appendChild(s);
      const img = document.createElement('img');
      img.src = 'https://example.invalid/x.png';
      document.body.appendChild(img);
      let fetchBlocked = false;
      try { await fetch('https://example.invalid/x'); } catch { fetchBlocked = true; }
      await new Promise((r) => setTimeout(r, 300));
      return {
        inlineRan: window.__inlineRan === true,
        fetchBlocked,
        directives: violations.slice(before).map((v) => v.split(' ')[0]),
      };
    })()`);

    expect(result.inlineRan).toBe(false);
    expect(result.fetchBlocked).toBe(true);
    // ガードの観測点（securitypolicyviolation）が実際に違反を拾う。
    expect(result.directives).toEqual(
      expect.arrayContaining(['script-src-elem', 'img-src', 'connect-src']),
    );
  });
});
