import { test, expect } from '@playwright/test';
import type { EdgeTypeCatalogItem } from '../src/lib/api/generated/bff.schemas';
import { installBffSession, sessionUser, expectBffTrafficIsComplete } from './support/bffSession';

// SC-18 (#917 / #1139): ナレッジグラフビュー（`/graph`）のスクリーンレベル・スモーク。
//
// 🔴 **未認証の往復ではパスの取り違えを見分けられない。** 未知パスの受け皿（catchAllRoute）は
// `RequireAuth` 配下に居るため、ルートを消しても未認証なら同じく `/login` へ行く（#918 の実測）。
// ルートの実在は、下のセッション付きの本体と `router.test.ts` が固定する。
//
// 🔴 **本画面は echarts の巨大チャンクを遅延で読む。** セッション付きで開いて初めて
// **成果物側の配線**（遅延チャンクの相対 URL・`modulepreload`）がこの層で踏まれる ——
// jsdom の単体テストでは見えない面である（IADR-0330 §棄却した案）。
//
// セッションの土台と限界（＝契約の写しであって後段ではない）は `support/bffSession.ts`。
// 図形・アイコン・線種の写像は Vitest（純関数 `graphOption`）が引き続き担う。

const edgeTypes: EdgeTypeCatalogItem[] = [
  { id: 'relates-to', name: '関連する', layer: 'core', isSymmetric: true },
];

test('unauthenticated visit to /graph redirects to /login', async ({ page }) => {
  await page.goto('/graph');

  // RequireAuth は遷移元を ?from= で保持する（IADR-0124 決定 3）。
  await expect(page).toHaveURL(/\/login(\?|$)/);
  await expect(page.getByRole('button', { name: /Keycloak/ })).toBeVisible();
});

test('SC-18: renders for any authenticated user and always keeps the help text', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    // 05_screens §SC-18 主アクター「全利用者」。**ロール限定は無い**（管理ロールを与えない）。
    user: sessionUser([]),
    handlers: { 'GET /graph/edge-types': edgeTypes },
  });

  await page.goto('/graph');

  // ★ 陽性対照: 見出し・左ナビ「ナレッジグラフ」・**常に出るヘルプ固定文言**
  //（ADR-0034 決定 2 の受け入れ済み副作用。0 件でないときにも出す）。
  await expect(page.getByRole('heading', { name: 'ナレッジグラフ', level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'ナレッジグラフ' })).toBeVisible();
  await expect(page.getByTestId('graph-help')).toBeVisible();

  // ★ 陰性対照: **文書を選ぶ前に近傍を取りに行かない**（探索は起点が決まってからである）。
  // 「何も出ない」だけでは実装が壊れている場合と区別できないため、**上の陽性対照と対で**読むこと。
  const keys = traffic.calls.map((c) => c.key);
  expect(keys.filter((k) => k.startsWith('GET /graph/neighbors'))).toEqual([]);

  expectBffTrafficIsComplete(traffic);
});
