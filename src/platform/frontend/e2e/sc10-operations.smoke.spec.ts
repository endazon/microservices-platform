import { test, expect } from '@playwright/test';
import type { DashboardSummaryDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-10 (#504 / #1139): 運用ダッシュボード（`/admin/ops`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 図の写像・期間切替・品質指標の丸めは Vitest（単体）が引き続き担う。

// 🔴 **0 件の要約を返す。** SLO とコストは契約に無く、画面も出さない（下の陰性対照）。
const summary: DashboardSummaryDto = {
  totalSearches: 0,
  totalAnswers: 0,
  usageTrend: [],
  topSearchTerms: [],
  quality: { up: 0, down: 0, total: 0, satisfactionRate: 0 },
};

test('unauthenticated visit to /admin/ops redirects to /login', async ({ page }) => {
  await page.goto('/admin/ops');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-10: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers: { 'GET /dashboard/summary': summary },
  });

  await page.goto('/admin/ops');

  // ★ 陽性対照: 画面が描かれ、左ナビ「ダッシュボード」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '運用ダッシュボード', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'ダッシュボード' })).toBeVisible();
  // ★ 陽性対照: **0 件は 0 件として言う。** 取得失敗を空表示へ寄せる実装はここで区別される。
  await expect(page.getByText('期間内の利用はありません。')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-10: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/ops');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '運用ダッシュボード' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'ダッシュボード' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /dashboard/summary');
  expectBffTrafficIsComplete(traffic);
});
