import { test, expect } from '@playwright/test';
import type { AttributeValuesResponse } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-08 (#134 / #1139): AI 分析ダッシュボード（`/analyze`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 分析依頼・結果・出典の中身は Vitest（単体）が引き続き担う。

// 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。
const attributeValues: AttributeValuesResponse = { values: [] };

test('unauthenticated visit to /analyze redirects to /login', async ({ page }) => {
  await page.goto('/analyze');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-08: renders for any authenticated user and requests no analysis before it is asked', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-08 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    // 🔴 SC-08 は左ナビ「管理」ではなく**「利用者」グループ**に置く（05_screens §共通シェル）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/analyze');

  // ★ 陽性対照: 見出し・副題・左ナビ「AI分析」。
  await expect(page.getByRole('heading', { name: 'AI分析依頼', level: 1 })).toBeVisible();
  await expect(page.getByText('範囲指定の分析依頼・結果・出典')).toBeVisible();
  await expect(page.getByRole('link', { name: 'AI分析' })).toBeVisible();

  // ★ 陰性対照: **開いただけで分析を投げない**（依頼は利用者の操作で初めて走る）。
  // 「何も出ない」だけでは実装が壊れている場合と区別できないため、**上の陽性対照と対で**読むこと。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys.filter((k) => k.startsWith('POST /analysis'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
