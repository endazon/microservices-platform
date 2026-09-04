import { test, expect } from '@playwright/test';
import type {
  DocumentContentDto,
  DocumentDto,
  WikiPageSummary,
} from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-03 (#502 / #1139): 文書詳細／プレビュー（`/docs/:id`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// **本画面は動的セグメント（`$id`）を持つため、取り違えの余地はいっそう大きい** ——
// ルートの実在とパラメータの受け渡しは、下のセッション付きの本体と `router.test.ts` が固定する。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 版・タグ・AI 提案パネルの中身は Vitest（単体）が引き続き担う。

const ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

const doc: DocumentDto = {
  id: ID,
  title: '経費精算マニュアル',
  status: 'published',
  version: 3,
  attributes: { department: 'sales' },
  tags: [],
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-02T00:00:00Z',
};

// 🔴 **本文には Markdown の見出しと生の HTML を両方入れる。** 画面は Markdown レンダラを持たず、
// 原文を等幅・改行保持で**そのまま**出す設計であり、下の陰性対照がそれを固定する。
const content: DocumentContentDto = {
  id: ID,
  title: '経費精算マニュアル',
  markdown: '## 申請の手順\n\n<b>領収書</b>を添付して申請する。',
};

// #1200 / UC-07: 「Wiki で閲覧」は権限内の Wiki 台帳にこの文書が載っているときだけ出る。
const wikiPage: WikiPageSummary = {
  id: 'page-1',
  documentId: ID,
  title: '経費精算マニュアル',
  slug: 'keihi-manual',
  wikiPath: `doc/${ID}`,
  status: 'Active',
  syncedAt: '2026-08-02T00:00:00Z',
};

test('unauthenticated visit to /docs/:id redirects to /login', async ({ page }) => {
  await page.goto(`/docs/${ID}`);

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-03: the id in the path reaches the fetch, and the body renders as normalized Markdown', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-03 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    // 🔴 本文が届くと**続けて**版履歴と AI 提案パネルが走る（本文が 5xx の間は走らない）。
    // 用意し忘れると `expectBffTrafficIsComplete` が落とす —— 実際にこの 3 件で落ちて気づいた。
    handlers: {
      [`GET /documents/${ID}`]: doc,
      [`GET /documents/${ID}/content`]: content,
      [`GET /documents/${ID}/versions`]: [],
      'GET /graph/suggestions': [],
      'GET /graph/edge-types': [],
      // #1200: 本文の下の「Wiki で閲覧」が引く権限内の Wiki 台帳（用意し忘れると `unhandled` で落ちる）。
      'GET /wiki/pages': [wikiPage],
    },
  });

  await page.goto(`/docs/${ID}`);

  // ★ 陽性対照: 見出しは**文書の題名**である（静的な画面名ではない）。
  // これが出るのは、**パスの `$id` が取得へ渡り、応答が描画へ届いた**ときだけである。
  await expect(page.getByRole('heading', { name: '経費精算マニュアル', level: 1 })).toBeVisible();
  await expect(page.getByText('正規化文書（Markdown）プレビュー')).toBeVisible();
  // 本文が届いている（`GET /documents/:id/content` の応答が描画へ繋がっている）。
  await expect(page.getByText('申請の手順')).toBeVisible();

  // ★ 陰性対照 1: **Markdown レンダラを置かない。** 原文を等幅・改行保持でそのまま出す設計であり、
  // `##` は見出しにならず、`<b>` はタグとして解釈されない —— **本文由来の HTML を描かない**という
  // 安全側の性質そのものである。レンダラを足すとここが落ちる。
  await expect(page.getByRole('heading', { name: '申請の手順' })).toHaveCount(0);
  await expect(page.locator('pre')).toContainText('## 申請の手順');
  await expect(page.locator('pre b')).toHaveCount(0);

  // ★ 陰性対照 2: 取得は**パスの id で**行う。取り違え（別 id・空 id）はここで落ちる。
  expect(traffic.calls.map((c) => c.key)).toContain(`GET /documents/${ID}`);

  // ★ 陽性対照（#1200 / UC-07）: 台帳に載る文書なので「Wiki で閲覧」が SC-04 の**文書別ディープリンク**へ出る。
  await expect(page.getByRole('link', { name: 'Wikiで閲覧' })).toHaveAttribute(
    'href',
    `/wiki?doc=${ID}`,
  );

  expectBffTrafficIsComplete(traffic);
});
