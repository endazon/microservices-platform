import { test, expect } from '@playwright/test';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-04 (#130 / #1139): Wiki 閲覧導線（`/wiki`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// 🔴 **Wiki.js そのものは別ホストであり、この層では踏まない**（SSO 遷移・ABAC 到達確認は
// Wiki.js ／ゲートウェイ側が担う。#118 で実測済み）。本 spec が固定するのは **SPA 側の導線**だけである。
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。

test('unauthenticated visit to /wiki redirects to /login', async ({ page }) => {
  await page.goto('/wiki');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-04: renders the hand-off for any authenticated user and fetches nothing of its own', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-04 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
  });

  await page.goto('/wiki');

  // ★ 陽性対照: 見出しと左ナビ「Wiki」（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: 'Wiki 閲覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Wiki' })).toBeVisible();

  // ★ 陰性対照: **本画面は自前の後段を持たない。** 身元と共通シェルの通知以外を叩き始めたら、
  // 別ホストの責務を SPA 側へ引き込んでいる（`handlers` を空にしてあるので、
  // 何か叩けば `unhandled` に載って下で落ちる）。
  expect(traffic.calls.map((c) => c.key).sort()).toEqual(['GET /auth/me', 'GET /notifications']);

  expectBffTrafficIsComplete(traffic);
});
