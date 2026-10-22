import { test as base, expect } from '@playwright/test';

// NFR / IADR-0578（Refs GHSA-5h4q-4m2j-rjhm）: **全 E2E を CSP の違反なしで走らせる**ための `test`。
//
// preview（`vite.config.ts`）は本番の配信（`Caddyfile`）と同じ CSP を返す。CSP の違反は画面を
// 静かに欠けさせる（スクリプト・スタイル・画像・通信が黙って拒まれる）だけで、テストの他の
// アサーションでは拾えないことがある。そこで**ページが報告した違反を 1 件でもあれば落とす**。
//
// - 拾い方は 2 つ: ページ内の `securitypolicyviolation` イベント（文書に届くもの）と、ブラウザが
//   出す「Content Security Policy」を含むコンソールのエラー（イベントにならない拒否も拾う）。
// - spec は `@playwright/test` の代わりにここから `test` / `expect` を import する。
// - 違反を**わざと**起こす spec（`csp.smoke.spec.ts` の陰性対照）は `allowCspViolations` で外す。

const VIOLATIONS_KEY = '__cspViolations';

export const test = base.extend<{ allowCspViolations: boolean; cspGuard: void }>({
  allowCspViolations: [false, { option: true }],
  cspGuard: [
    async ({ page, allowCspViolations }, use) => {
      const fromConsole: string[] = [];
      page.on('console', (message) => {
        if (message.type() === 'error' && /Content Security Policy/i.test(message.text())) {
          fromConsole.push(message.text());
        }
      });
      // e2e の tsconfig は DOM の型を持たないため、ページ側のコードは文字列で渡す。
      await page.addInitScript(
        `(() => { const b = []; window.${VIOLATIONS_KEY} = b; document.addEventListener('securitypolicyviolation', (e) => b.push(e.effectiveDirective + ' ' + e.blockedURI)); })();`,
      );

      await use();

      if (allowCspViolations) return;
      const fromPage = page.isClosed()
        ? []
        : await page
            .evaluate<string[]>(`window.${VIOLATIONS_KEY} ?? []`)
            .catch(() => [] as string[]);
      expect([...fromPage, ...fromConsole], 'CSP の違反を観測しました').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
