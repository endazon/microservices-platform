import { test, expect } from '@playwright/test';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-09 (#504 / #1139): 管理者設定（ABAC）（`/admin/abac`）のスクリーンレベル・スモーク。
//
// 🔴 **本画面は platform-admin 限定である**（05_screens §SC-09。**運用者も不可**）。
// 権限外では `RequireRole` が `NotFound` を描き、画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// 🔴 **陰性対照に「ロール無し」ではなく運用者を使う。** SC-05〜SC-07 / SC-10 / SC-11 は
// 運用者にも開くため、`anyOf` へ operator が紛れ込んでも「ロール無し」では落ちない ——
// **区別できるのは運用者を当てたときだけ**である。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない**（catch-all が認証ガード配下。#918）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 属性辞書・ポリシー定義・検証結果は Vitest（単体）が引き続き担う。

const handlers = { 'GET /admin/authz/attributes': [], 'GET /admin/authz/policies': [] };

test('unauthenticated visit to /admin/abac redirects to /login', async ({ page }) => {
  await page.goto('/admin/abac');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-09: a platform administrator reaches the screen and its navigation entry', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers,
  });

  await page.goto('/admin/abac');

  // ★ 陽性対照: 画面が描かれ、左ナビ「ABAC設定」も出る（05_screens §共通シェル）。
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'ABAC設定' })).toBeVisible();

  expectBffTrafficIsComplete(traffic);
});

// SC-09, FR-17, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): **辺の型辞書の区画を実ブラウザで固定する。**
//
// 計画 §SC-09 は 4 区画を定めており、**辺の型だけが長く欠けていた**（前提 ADR の保留が
// 2026-08-07 に解けたのに、解除を実行する主体が居なかった）。**区画の実在をここで固定する** ——
// 単体テストは合成したツリーを見るが、こちらは**実ビルド成果物とルータの上で**見る。
//
// 🔴 **画面が引くのは `/bff/edge-types`（使用件数つき・admin ＋ operator）であって、
// SC-03 / SC-18 / SC-21 が使う `/bff/graph/edge-types`（描画用カタログ・認証のみ・件数なし）ではない。**
// 後者しか用意しないので、**取り違えた実装は `unhandled` に載って落ちる**
// （`expectBffTrafficIsComplete` が担保する）。
test('SC-09: the edge-type dictionary tab reads the admin dictionary, not the drawing catalog', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser(['platform-admin']),
    handlers: {
      'GET /admin/authz/attributes': [],
      'GET /admin/authz/policies': [],
      'GET /edge-types': [
        {
          id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
          name: 'cites',
          layer: 'core',
          isSymmetric: false,
          isSeed: true,
          usageCount: 342,
        },
      ],
    },
  });

  await page.goto('/admin/abac');
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）', level: 1 })).toBeVisible();

  // ★ 陽性対照: 4 区画のうち「辺の型」が在る（＝下の主張が「画面が描けていないだけ」ではない）。
  await page.getByRole('tab', { name: '辺の型' }).click();

  await expect(page.getByRole('table', { name: '辺の型辞書の一覧' })).toBeVisible();
  await expect(page.getByText('cites')).toBeVisible();
  // **使用件数が届く。** カタログ（件数なし）へ向いていればここで落ちる。
  await expect(page.getByRole('cell', { name: '342' })).toBeVisible();

  // ★ 陰性対照: 契約の無い「逆向きの表示語」の列は作っていない（#1241 / [[IADR-0387]] 決定 5）。
  await expect(page.getByRole('columnheader', { name: '逆向きの表示語' })).toHaveCount(0);

  // 取り違えの検査: 描画用カタログは 1 度も呼ばれていない。
  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /graph/edge-types');

  expectBffTrafficIsComplete(traffic);
});
test('SC-09: an operator gets the same not-found page and never learns the screen exists', async ({
  page,
}) => {
  // ★ 陰性対照: **応答を 1 つも用意しない。** 管理端点を呼べば `unhandled` に載って落ちる。
  const traffic = await installBffSession(page, { user: sessionUser(['platform-operator']) });

  await page.goto('/admin/abac');

  // IADR-0009: 不在も権限による秘匿も同じ画面で応答する。
  await expect(page.getByRole('heading', { name: '見つかりませんでした' })).toBeVisible();
  await expect(page.getByRole('heading', { name: '管理者設定（ABAC）' })).toHaveCount(0);
  // 左ナビにも項目が出ない（出た時点で「その画面がある」ことが漏れる）。
  await expect(page.getByRole('link', { name: 'ABAC設定' })).toHaveCount(0);

  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /admin/authz/policies');
  expectBffTrafficIsComplete(traffic);
});
