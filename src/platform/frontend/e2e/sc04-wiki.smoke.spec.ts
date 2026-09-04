import { test, expect } from '@playwright/test';
import type {
  WikiPageSummary,
  WikiPageView,
  WikiSearchHit,
} from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

// SC-04 (#130 → #1200 / #1139): Wiki 閲覧画面（`/wiki`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// **［2026-09-03 / #1200］画面は Wiki.js への外部リンク 1 本ではなくなった。** ADR-0073 決定 2 により
// ページツリー・本文・検索を `/bff/wiki/*` 経由で SPA が描く。本 spec は**ツリー → 本文 → 検索の導線**が
// 実ブラウザ・実ビルド成果物の上で繋がることと、**Wiki.js 本体 UI への外部リンクが 1 本も無い**ことを固定する。
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 一覧・本文・検索それぞれの空／404／502 の描き分けは Vitest（単体）が担う。

const DOC_A = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const DOC_B = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

const pages: WikiPageSummary[] = [
  {
    id: 'page-a',
    documentId: DOC_A,
    title: '経費精算規程',
    slug: 'keihi-seisan',
    wikiPath: `doc/${DOC_A}`,
    status: 'Active',
    syncedAt: '2026-09-01T00:00:00Z',
  },
  {
    id: 'page-b',
    documentId: DOC_B,
    title: '旅費規程',
    slug: 'ryohi',
    wikiPath: `doc/${DOC_B}`,
    status: 'Active',
    syncedAt: '2026-09-01T00:00:00Z',
  },
];

// 🔴 **本文は Wiki.js が描画した HTML である。** SC-03（Markdown 原文をそのまま出す）と対照的に、
// ここでは `<h2>` が見出しとして描かれる。落ちる側（script / img）も同じ本文に入れて対で見る。
const view: WikiPageView = {
  ...pages[0],
  content:
    '<h2>申請の手順</h2><p><b>領収書</b>を添付して申請する。</p>' +
    '<script>window.__pwned = 1</script><img src="https://evil.example/x.png" alt="x">',
};

const hits: WikiSearchHit[] = [
  {
    id: 'page-b',
    documentId: DOC_B,
    title: '旅費規程',
    slug: 'ryohi',
    wikiPath: `doc/${DOC_B}`,
    syncedAt: '2026-09-01T00:00:00Z',
  },
];

test('unauthenticated visit to /wiki redirects to /login', async ({ page }) => {
  await page.goto('/wiki');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-04: draws the page tree, opens a page and searches — all through the BFF, inside the SPA', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-04 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: {
      'GET /wiki/pages': pages,
      'GET /wiki/pages/keihi-seisan': view,
      'GET /wiki/search': hits,
    },
  });

  await page.goto('/wiki');

  // ★ 陽性対照: 見出しと左ナビ「Wiki」（05_screens §共通シェル）、権限内の 2 件が並ぶツリー。
  await expect(page.getByRole('heading', { name: 'Wiki 閲覧', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Wiki', exact: true })).toBeVisible();
  const tree = page.getByRole('navigation', { name: 'ページツリー' });
  await expect(tree.getByRole('link')).toHaveCount(2);

  // ★ 陰性対照 1: 選ぶ前に本文を取りに行かない（一覧だけで済ませる）。
  expect(traffic.calls.map((c) => c.key)).not.toContain('GET /wiki/pages/keihi-seisan');

  // ツリー → 本文。**パスの `page` が取得へ渡り、Wiki.js の HTML が描画へ届く。**
  await tree.getByRole('link', { name: '経費精算規程' }).click();
  await expect(page).toHaveURL(/\/wiki\?page=keihi-seisan/);
  await expect(page.getByRole('heading', { name: '申請の手順' })).toBeVisible();
  const body = page.getByTestId('wiki-page-content');
  await expect(body.locator('b')).toHaveText('領収書');
  // ★ 陰性対照 2: sanitize（IADR-0367 決定 3）。スクリプトと画像は届かない。
  await expect(body.locator('script')).toHaveCount(0);
  await expect(body.locator('img')).toHaveCount(0);
  // e2e の tsconfig は DOM lib を持たないので `window` ではなく `globalThis` で読む（ブラウザ側で評価される）。
  expect(
    await page.evaluate(() => (globalThis as unknown as { __pwned?: number }).__pwned),
  ).toBeUndefined();
  // 文書詳細（SC-03）への復帰リンク。
  await expect(page.getByRole('link', { name: '文書詳細へ戻る' })).toHaveAttribute(
    'href',
    `/docs/${DOC_A}`,
  );
  // パンくず `ホーム / Wiki / <題名>`（05_screens §共通シェル）。「Wiki」は親の段（自画面へのリンク）、
  // 葉は画面が取得後に渡す。**自画面の段に固定の名を置くと葉が一度も描かれない**（稼働環境で実測。#1200）。
  const crumb = page.getByRole('navigation', { name: 'パンくず' });
  await expect(crumb.getByRole('link', { name: 'Wiki', exact: true })).toHaveAttribute(
    'href',
    '/wiki',
  );
  await expect(crumb.locator('[aria-current="page"]')).toHaveText('経費精算規程');

  // 検索 → 結果（権限内のみ。並びは Wiki.js の関連度順）。
  await page.getByLabel('Wiki を検索').fill('旅費');
  await page.getByRole('button', { name: '検索', exact: true }).click();
  await expect(page).toHaveURL(/q=/);
  await expect(
    page.getByRole('list', { name: '検索結果' }).getByRole('link', { name: '旅費規程' }),
  ).toBeVisible();
  const searchCall = traffic.calls.find((c) => c.key === 'GET /wiki/search');
  expect(searchCall?.search).toContain('q=');

  // ★ 陰性対照 3: **Wiki.js 本体 UI への外部リンクは 1 本も無い**（ADR-0073 決定 1 / #1200 受け入れ基準）。
  await expect(page.locator('a[target="_blank"]')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('SC-04: a page outside the permitted ledger is shown as not found while the tree still renders', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /wiki/pages': pages,
      // 権限外・不存在・アーカイブ済みは同じ 404（存在秘匿。IADR-0009 / IADR-0355 決定 5）。
      'GET /wiki/pages/secret': reply(404, {}),
    },
  });

  await page.goto('/wiki?page=secret');

  // ★ 陰性対照: 中立の文。alert（サーバ故障の形）にはならない。
  await expect(page.getByText('ページが見つかりませんでした。')).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  // ★ 陽性対照: 権限内の一覧は変わらず描かれる。
  await expect(
    page.getByRole('navigation', { name: 'ページツリー' }).getByRole('link'),
  ).toHaveCount(2);

  expectBffTrafficIsComplete(traffic);
});
