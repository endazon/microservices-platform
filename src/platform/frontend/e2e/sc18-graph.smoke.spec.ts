import { test, expect } from './support/cspGuard';
import type { EdgeTypeCatalogItem, GraphView } from '../src/lib/api/generated/bff.schemas';
import {
  installBffSession,
  sessionUser,
  expectBffTrafficIsComplete,
  reply,
} from './support/bffSession';

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

// ───────────── M-054・M-055（#2021 / E-16）: 探索深さ・型フィルタ・上限帯・空状態 2 種・凡例 ─────────────
//
// 期待値は計画 05_screens §SC-18 から起こした。
// - 探索深さ（hops）は 1 / 2 / 3 のみ・既定 2。上限超過は丸めずエラー（後段は 400）。
// - 辺の種別フィルタはサーバ側で適用する（要求に載せる。クライアントで打ち切り後に絞らない）。
// - 上限到達時はグラフ領域の上部に「上位 N 件を表示（全 M 件）」の帯を出す。「もっと読み込む」は置かない。
// - 空状態は「関係する文書がありません」と「権限のある文書がありません」の 2 種を描き分ける。
// - ノードの種別（組織文書／個人資料）と辺の型は、色だけでなく形・線種とラベルの凡例で判る。

const ROOT = '11111111-2222-4333-8444-555555555555';
const OTHER = '66666666-7777-4888-8999-000000000000';
const graphTypes: EdgeTypeCatalogItem[] = [
  { id: 'aaaaaaaa-0000-4000-8000-000000000001', name: 'related', layer: 'core', isSymmetric: true },
  { id: 'aaaaaaaa-0000-4000-8000-000000000002', name: 'cites', layer: 'core', isSymmetric: false },
];
const neighborhood = (overrides: Partial<GraphView> = {}): GraphView => ({
  nodes: [
    { documentId: ROOT, title: '経費精算マニュアル' },
    { documentId: OTHER, title: '出張旅費規程' },
  ],
  edges: [
    {
      id: 'e-1',
      sourceDocumentId: ROOT,
      targetDocumentId: OTHER,
      edgeTypeId: graphTypes[1].id,
      provenance: 'auto',
    },
  ],
  truncated: false,
  ...overrides,
});

test('M-054: 探索深さは 1/2/3（既定 2）だけで、深さと辺の型の絞り込みはサーバへの要求に載る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      [`GET /graph/${ROOT}/neighbors`]: neighborhood(),
    },
  });

  await page.goto(`/graph?root=${ROOT}`);
  await expect(page.getByText('起点: 経費精算マニュアル')).toBeVisible();
  // 陽性対照: グラフ領域が描かれている（下の 400 のテストは、これが 0 件であることを見る）。
  await expect(
    page.getByRole('img', { name: /ナレッジグラフ（ノード 2 件・辺 1 本）/ }),
  ).toBeVisible();

  // ★ 選択肢の全集合と既定。
  const hops = page.getByRole('combobox', { name: '探索深さ（hops）' });
  await expect(hops.getByRole('option')).toHaveText(['1', '2（既定）', '3（上限）']);
  await expect(hops).toHaveValue('2');
  const calls = () =>
    traffic.calls
      .filter((c) => c.key === `GET /graph/${ROOT}/neighbors`)
      .map((c) => new URLSearchParams(c.search));
  expect(calls()[0].get('hops')).toBe('2');
  expect(calls()[0].has('types')).toBe(false);

  // ★ 深さを変えると、その値で引き直す。
  await hops.selectOption('3');
  await expect.poll(() => calls().length).toBe(2);
  expect(calls()[1].get('hops')).toBe('3');

  // ★ 辺の型を外すと、残した型がサーバへの要求に載る（クライアントで間引かない）。
  const filter = page.getByTestId('edge-type-filter');
  await filter.getByRole('checkbox', { name: 'related' }).uncheck();
  await expect.poll(() => calls().length).toBe(3);
  expect(calls()[2].get('types')).toBe(graphTypes[1].id);
  expect(calls()[2].get('hops')).toBe('3');

  expectBffTrafficIsComplete(traffic);
});

test('M-054: URL の探索深さが範囲外（hops=4）なら、丸めずにエラーを示し、近傍の要求を送らない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      [`GET /graph/${ROOT}/neighbors`]: neighborhood(),
    },
  });

  await page.goto(`/graph?root=${ROOT}&hops=4`);
  // ★ 範囲外であることを文言で示す（既定の 2 へ黙って丸めない）。
  await expect(
    page.getByRole('alert').filter({ hasText: '探索深さ（hops）は 1・2・3 のいずれかです。' }),
  ).toBeVisible();
  // 陽性対照: 画面そのものは描けている（見出しと探索深さの欄）。
  await expect(page.getByRole('heading', { name: 'ナレッジグラフ', level: 1 })).toBeVisible();
  const hops = page.getByRole('combobox', { name: '探索深さ（hops）' });
  await expect(hops).toBeVisible();
  await expect(hops).not.toHaveValue('2');
  // ★ 丸めた深さで探索しない（近傍を 1 度も引かない）。
  expect(traffic.calls.filter((c) => c.key.endsWith('/neighbors'))).toEqual([]);

  // 選び直せば、その深さで探索する。
  await hops.selectOption('3');
  await expect(page.getByRole('alert').filter({ hasText: '探索深さ（hops）は' })).toHaveCount(0);
  await expect
    .poll(() => traffic.calls.filter((c) => c.key === `GET /graph/${ROOT}/neighbors`).length)
    .toBe(1);
  expect(
    new URLSearchParams(
      traffic.calls.find((c) => c.key === `GET /graph/${ROOT}/neighbors`)?.search,
    ).get('hops'),
  ).toBe('3');

  expectBffTrafficIsComplete(traffic);
});

// クライアント側の検証（範囲外の hops を丸めずエラーにし、要求を送らない）は、単体テスト（GraphViewPage.test.tsx）と
// 上の M-054「URL の探索深さが範囲外（hops=4）なら…」で固定してある。ここは後段の防壁（400）を見る試験である。
test('M-054: 後段が探索深さを 400 で拒否したら、丸めたグラフを描かずにエラーを示す', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      [`GET /graph/${ROOT}/neighbors`]: reply(400, {
        title: 'Bad Request',
        status: 400,
        detail: 'hops は 1〜3 です',
      }),
    },
  });

  await page.goto(`/graph?root=${ROOT}&hops=3`);
  await expect(page.getByText('グラフを読み込めませんでした。')).toBeVisible();
  // グラフも空状態も描かない（「関係が無い」と取り違えさせない）。
  await expect(page.getByTestId('empty-no-relations')).toHaveCount(0);
  await expect(page.getByRole('img', { name: /ナレッジグラフ（ノード/ })).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-054: 表示上限で打ち切られたら「上位 N 件を表示（全 M 件）」の帯を出し、もっと読み込む操作は置かない', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      [`GET /graph/${ROOT}/neighbors`]: neighborhood({
        truncated: true,
        totalNodes: 1432,
        totalEdges: 3100,
        totalIsLowerBound: false,
      }),
    },
  });

  await page.goto(`/graph?root=${ROOT}`);
  const banner = page.getByTestId('truncation-banner');
  await expect(banner).toContainText(/上位 2 件を表示（全 1,?432 件）/);
  await expect(page.getByRole('button', { name: /もっと|さらに読み込む/ })).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-055: 空状態は「関係する文書がありません」と「権限のある文書がありません」を描き分ける', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      // 起点はあるが辺が無い。
      [`GET /graph/${ROOT}/neighbors`]: neighborhood({
        edges: [],
        nodes: [{ documentId: ROOT, title: '経費精算マニュアル' }],
      }),
      // 権限外・不在（区別しない 404）。
      [`GET /graph/${OTHER}/neighbors`]: reply(404, { title: 'Not Found', status: 404 }),
    },
  });

  await page.goto(`/graph?root=${ROOT}`);
  await expect(page.getByTestId('empty-no-relations')).toContainText('関係する文書がありません');
  await expect(page.getByTestId('empty-denied')).toHaveCount(0);

  await page.goto(`/graph?root=${OTHER}`);
  await expect(page.getByTestId('empty-denied')).toContainText('権限のある文書がありません');
  // 存在しないのか権限が無いのかは区別できない旨を添える。
  await expect(page.getByTestId('empty-denied')).toContainText(
    '文書が存在しないのか、閲覧権限がないのかは区別できません。',
  );
  await expect(page.getByTestId('empty-no-relations')).toHaveCount(0);

  expectBffTrafficIsComplete(traffic);
});

test('M-055: ノードの種別と辺の型は、色だけでなく形・線種のラベルを持つ凡例で判る', async ({
  page,
}) => {
  const traffic = await installBffSession(page, {
    user: sessionUser([]),
    handlers: {
      'GET /graph/edge-types': graphTypes,
      [`GET /graph/${ROOT}/neighbors`]: neighborhood(),
    },
  });

  await page.goto(`/graph?root=${ROOT}`);
  await expect(page.getByText('起点: 経費精算マニュアル')).toBeVisible();
  // ★ ノードの種別（形）と、中核 5 種の辺の型（線種）のラベル。
  for (const label of [
    '円 = 組織文書',
    '角丸四角（破線の輪郭）= 個人資料',
    '太い輪郭・大きい表示 = 起点',
    'related: 実線・細（向きなし）',
    'cites: 実線・矢印',
    'supersedes: 太実線・矢印（旧→新）',
    'derived-from: 破線・矢印',
    'embeds: 点線・矢印',
    'AI 提案由来の辺は破線（承認済みのみ表示）',
  ]) {
    // 項目は記号（📄・線の見本。読み上げない）とラベルで組まれるので、ラベルを部分一致で引く。
    await expect(page.getByTestId(/^legend-/).getByText(label)).toBeVisible();
  }

  expectBffTrafficIsComplete(traffic);
});
