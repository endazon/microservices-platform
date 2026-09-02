import { test, expect } from '@playwright/test';
import type { AttributeValuesResponse } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-01 (#127 / #1139): 検索・チャット質問（`/ask`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 検索・SSE ストリーミング・出典・フィードバックの挙動は Vitest（単体）が引き続き担う
// ——**本 spec が SSE を張らないのは、それが単体の担当だからである。踏めないからではない。**

// 対象範囲フィルタが引く属性の候補値（SC-01 / SC-08 が共有する部品）。
const attributeValues: AttributeValuesResponse = { values: [] };

test('unauthenticated visit to /ask redirects to /login', async ({ page }) => {
  await page.goto('/ask');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-01: renders for any authenticated user and streams no answer before it is asked', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-01 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'POST /attribute-values': attributeValues },
  });

  await page.goto('/ask');

  // ★ 陽性対照: 見出しと副題（05_screens §SC-01 主要素 1）。
  await expect(page.getByRole('heading', { name: 'ナレッジ検索・AI質問', level: 1 })).toBeVisible();
  await expect(
    page.getByText('横断検索と根拠付きAI回答（ストリーミング・出典表示）'),
  ).toBeVisible();
  // ★ 陽性対照: 左ナビ「検索・質問」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('link', { name: '検索・質問' })).toBeVisible();

  // ★ 陰性対照: **問う前に回答の口を開けない。** 初期表示で `POST /search` も回答ストリームも
  // 走らないこと（走る実装は「開いただけで LLM を叩く」——ADR-0032 の趣旨に反する）。
  // 「何も出ない」だけでは実装が壊れている場合と区別できないため、**上の陽性対照と対で**読むこと。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys).not.toContain('POST /search');
  expect(keys.filter((k) => k.includes('ask'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
