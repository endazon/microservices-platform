import { test, expect } from '@playwright/test';
import type { DocumentDto, TagDictionaryResponse } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-05 (#503 / #1139): 文書管理（`/admin/documents`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin / platform-operator 限定である**（IADR-0039）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009）。
// **この出し分けは未認証のスモークでは 1 度も踏まれない** —— ロールを与えて初めて分岐する。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝これは契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧の中身・並び・エラー状態は Vitest（単体）が引き続き担う。

const doc: DocumentDto = {
  id: 'doc-1',
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: ['tag-1'],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

const tags: TagDictionaryResponse = { tags: [{ id: 'tag-1', name: '経費', usageCount: 1 }] };

const handlers = { 'GET /documents': [doc], 'GET /tags': tags };

test('unauthenticated visit to /admin/documents redirects to /login', async ({ page }) => {
  await page.goto('/admin/documents');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-05: an operator reaches the screen and its navigation entry', async ({ page }) => {
  const traffic = await installBffSession(page, {
    // 🔴 **運用者で測る。** 管理者だけで測ると `anyOf` から operator が落ちても気づけない。
    user: sessionUser(['platform-operator']),
    handlers,
  });

  await page.goto('/admin/documents');

  // ★ 陽性対照: 画面が描かれ、左ナビ「文書管理」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '文書一覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: '文書管理' })).toBeVisible();
  await expect(page.getByRole('cell', { name: '経費精算マニュアル' })).toBeVisible();

  // ★ 陰性対照: 空状態の文言は**行があるときに出てはならない**
  // （取得できた一覧を 0 件表示へ寄せる実装がここで落ちる）。
  await expect(page.getByText('文書はありません。')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-05: a user with no administrative role gets the same not-found page', async ({ page }) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼んでしまえば `unhandled` に載り、
  // 下の `expectBffTrafficIsComplete` が落ちる —— 「権限が無いのに取りに行った」を検出する。
  const traffic = await installBffSession(page, { user: sessionUser([]) });

  await page.goto('/admin/documents');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '文書一覧' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: '文書管理' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /documents');
  expectBffTrafficIsComplete(traffic);
});
