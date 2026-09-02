import { test, expect } from '@playwright/test';
import type { DriftReportDto, EffectiveConfigDto } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-11 (#504 / #1139): 構成ビューア（`/admin/config-viewer`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない。**
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// ドリフト明細・適用履歴・段の描画は Vitest（単体）が引き続き担う。

const effective: EffectiveConfigDto = {
  version: { gitCommit: 'abc1234', appliedAt: '2026-08-01T00:00:00Z', appliedBy: 'ops' },
  pipeline: [],
  eventBindings: [],
  ports: [],
  connectors: [],
};

const drift: DriftReportDto = { hasDrift: false, checkedAt: '2026-08-02T00:00:00Z', findings: [] };

const handlers = {
  'GET /admin/config': effective,
  'GET /admin/config/drift': drift,
  'GET /admin/config/history': [],
};

test('unauthenticated visit to /admin/config-viewer redirects to /login', async ({ page }) => {
  await page.goto('/admin/config-viewer');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-11: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers,
  });

  await page.goto('/admin/config-viewer');

  // ★ 陽性対照: 画面が描かれ、左ナビ「構成ビューア」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '実効構成', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '構成ビューア' })).toBeVisible();
  // ★ 陽性対照: **0 件は 0 件として言う。** 取得失敗を空表示へ寄せる実装はここで区別される。
  // 見るのは既定で開いている区画 (1) の中身にする（適用履歴の区画は既定で畳まれており、
  // **DOM には在るが不可視**である —— `toBeVisible` で実測した）。
  await expect(page.getByText('イベント接続はありません。')).toBeVisible();

  // ★ 陰性対照: ヘッダの版・ドリフトのバッジは**実効構成が取れたときだけ**出す（IADR-0129 決定 5）。
  // ここでは取れているので出る —— 逆に「取れていないのにバッジだけ残る」実装はこの対で落ちる。
  await expect(page.getByText('abc1234')).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

test('SC-11: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/config-viewer');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '実効構成' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: '構成ビューア' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/config');
  expectBffTrafficIsComplete(traffic);
});
