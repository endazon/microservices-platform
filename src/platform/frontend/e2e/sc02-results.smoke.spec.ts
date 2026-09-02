import { test, expect } from '@playwright/test';
import type { SearchResponse } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-02 (#502 / #1139): 検索結果一覧（`/search`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧の中身・SC-03 への遷移は Vitest（単体＋導線テスト searchFlow.test.tsx）が引き続き担う。

const empty: SearchResponse = { results: [], totalHits: 0, elapsedMs: 3 };

test('unauthenticated visit to /search redirects to /login', async ({ page }) => {
  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-02: an authenticated user sees the neutral empty answer, not a permission hint', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-02 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /search': empty },
  });

  await page.goto('/search?q=%E7%B5%8C%E8%B2%BB');

  // ★ 陽性対照: 見出しと左ナビ「結果一覧」（05_screens §共通シェル）。検索語が URL から復元され、
  // **開いた時点で問い合わせが走る**（下の traffic で確かめる）。
  await expect(page.getByRole('heading', { name: '検索結果一覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '結果一覧' })).toBeVisible();
  expect(traffic.calls.map((c) => c.key)).toContain('POST /search');

  // ★ 陽性対照: 0 件のときの固定文言（deny-by-default。IADR-0009）。
  await expect(page.getByText('該当する文書が見つかりませんでした。')).toBeVisible();

  // ★ 陰性対照: **権限外であることを匂わせない**（存在秘匿）。「権限」「閲覧できません」と
  // 書いた瞬間に「在るが見せない」が漏れる —— 0 件と権限外は同じ文言で応答する。
  await expect(page.getByText(/権限/)).toHaveCount(0);
  await expect(page.getByText(/閲覧できません/)).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});
