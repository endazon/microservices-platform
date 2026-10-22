import { test as base, expect } from '@playwright/test';

// NFR / IADR-0579（Refs GHSA-5h4q-4m2j-rjhm）: **全 E2E を CSP の違反なしで走らせる**ための `test`。
//
// preview（`vite.config.ts`）は本番の配信（`Caddyfile`）と同じ CSP を返す。CSP の違反は画面を
// 静かに欠けさせる（スクリプト・スタイル・画像・通信が黙って拒まれる）だけで、テストの他の
// アサーションでは拾えないことがある。そこで**ページが報告した違反を 1 件でもあれば落とす**。
//
// - 拾い方は 2 つ: ページ内の `securitypolicyviolation` イベント（文書に届くもの）と、ブラウザが
//   出す「Content Security Policy」を含むコンソールのエラー（イベントにならない拒否も拾う）。
// - 🔴 **違反はその場でノード側へ送る**（`page.exposeBinding`）。ページ内の変数に溜めると、文書をまたぐ
//   遷移（ログインの往復・`page.goto`）で遷移前の違反が消える。
// - spec は `@playwright/test` の代わりにここから `test` / `expect` を import する。
// - 違反を**わざと**起こす spec（`csp.smoke.spec.ts` の陰性対照）は `allowCspViolations` で外し、
//   観測した違反は `cspViolations` で読む。

const BINDING = '__reportCspViolation';

export const test = base.extend<{
  allowCspViolations: boolean;
  cspViolations: string[];
  cspGuard: void;
}>({
  allowCspViolations: [false, { option: true }],
  // 第 2 引数は Playwright の慣用では `use` だが、react-hooks の規則が React の `use` と取り違えるため名前を変える。
  cspViolations: async ({ page }, provide) => {
    const violations: string[] = [];
    page.on('console', (message) => {
      if (message.type() === 'error' && /Content Security Policy/i.test(message.text())) {
        violations.push(`console ${message.text()}`);
      }
    });
    await page.exposeBinding(BINDING, (_source, violation: string) => {
      violations.push(violation);
    });
    // e2e の tsconfig は DOM の型を持たないため、ページ側のコードは文字列で渡す。
    await page.addInitScript(
      `document.addEventListener('securitypolicyviolation', (e) => { void window.${BINDING}(e.effectiveDirective + ' ' + e.blockedURI); });`,
    );
    await provide(violations);
  },
  cspGuard: [
    async ({ cspViolations, allowCspViolations }, provide) => {
      await provide();
      if (allowCspViolations) return;
      expect(cspViolations, 'CSP の違反を観測しました').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
