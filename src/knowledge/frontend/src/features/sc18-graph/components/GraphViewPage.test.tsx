import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-18, UC-10, FR-17/FR-05 (#917): ナレッジグラフビュー。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる。echarts は echartsGraphLoader 1 本に閉じてあるので、そこもモックする
// （jsdom で実描画はできない。描き分けの判断は graphOption.test が固定する）。
const mocks = vi.hoisted(() => ({
  apiRequest: vi.fn(),
  setOption: vi.fn(),
  clickHandlers: [] as Array<(params: unknown) => void>,
}));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));
vi.mock('../../../lib/echarts/echartsGraphLoader', () => ({
  loadGraphECharts: () =>
    Promise.resolve({
      init: () => ({
        setOption: mocks.setOption,
        dispose: () => {},
        resize: () => {},
        on: (event: string, handler: (params: unknown) => void) => {
          if (event === 'click') mocks.clickHandlers.push(handler);
        },
        dispatchAction: () => {},
      }),
    }),
}));

import { createSc18GraphRoute } from '../routes/sc18GraphRoute';

const ROOT = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const LEAF = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const NOTE = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const TYPE_CITES = '11111111-1111-1111-1111-111111111111';
const TYPE_RELATED = '22222222-2222-2222-2222-222222222222';

const EDGE_TYPES = [
  { id: TYPE_CITES, name: 'cites', layer: 'core', isSymmetric: false },
  { id: TYPE_RELATED, name: 'related', layer: 'core', isSymmetric: true },
];

const VIEW = {
  nodes: [
    { documentId: ROOT, title: '経費精算規程 v3.2', isPrivateNote: false },
    { documentId: LEAF, title: '旅費規程', isPrivateNote: false },
    { documentId: NOTE, title: '自分の下書きメモ', isPrivateNote: true },
  ],
  edges: [
    {
      id: 'e1',
      sourceDocumentId: ROOT,
      targetDocumentId: LEAF,
      edgeTypeId: TYPE_CITES,
      provenance: 'auto',
    },
    {
      id: 'e2',
      sourceDocumentId: ROOT,
      targetDocumentId: NOTE,
      edgeTypeId: TYPE_RELATED,
      provenance: 'user',
    },
  ],
  truncated: false,
  totalNodes: 3,
  totalEdges: 2,
  totalIsLowerBound: false,
};

const DETAIL = {
  id: LEAF,
  title: '旅費規程',
  status: 'published',
  markdownUri: null,
  version: 2,
  attributes: { confidentiality: 'internal' },
  tags: ['経理'],
  createdAt: '2026-01-10T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
};

/** BFF の 3 面（辺の型辞書 / 近傍探索 / 文書詳細）へ応答を割り当てる。 */
function respond({
  view = VIEW as unknown,
  detail = DETAIL as unknown,
  edgeTypes = EDGE_TYPES as unknown,
}: { view?: unknown; detail?: unknown; edgeTypes?: unknown } = {}) {
  const reply = (value: unknown) =>
    value instanceof Error ? Promise.reject(value) : Promise.resolve(jsonResponse(value));
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す。実測）。
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path.includes('/graph/edge-types')) return reply(edgeTypes);
    if (path.includes('/neighbors')) return reply(view);
    if (path.includes('/documents/')) return reply(detail);
    return reply(view);
  });
}

/** apiRequest が受けた近傍探索のパス（最後の 1 件）。 */
function lastNeighborsPath(): string | undefined {
  const calls = mocks.apiRequest.mock.calls
    .map((c) => c[0] as string)
    .filter((p) => p.includes('/neighbors'));
  return calls[calls.length - 1];
}

async function renderPage(initialEntry = `/graph?root=${ROOT}`) {
  return renderUnitRoute((shell) => [createSc18GraphRoute(shell)], { initialEntry });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.setOption.mockReset();
  mocks.clickHandlers.length = 0;
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('GraphViewPage (SC-18)', () => {
  // SC-18 主要素 1・3・4・7 と凡例: グラフ・コントロール・凡例が揃って描画される。
  it('renders the graph with controls, edge type filter and legend', async () => {
    respond();
    await renderPage();

    expect(await screen.findByTestId('graph-canvas')).toBeInTheDocument();
    expect(screen.getByLabelText('探索深さ（hops）')).toHaveValue('2');
    expect(screen.getByLabelText('間引きの基準')).toHaveValue('distance');
    expect(screen.getByTestId('edge-type-filter')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'cites' })).toBeChecked();
    expect(screen.getByTestId('legend-nodes')).toHaveTextContent('円 = 組織文書');
    expect(screen.getByTestId('legend-edges')).toHaveTextContent('supersedes');
    // 描画へ渡った option（判断の内訳は graphOption.test が固定する）。
    await waitFor(() => expect(mocks.setOption).toHaveBeenCalled());
  });

  // ADR-0034 決定 2 の受け入れ済み副作用: ヘルプ固定文言は**結果が 0 件でないときにも常に**出す。
  it('always shows the help text even when the graph is not empty', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    expect(screen.getByTestId('graph-help')).toHaveTextContent(
      '関係が存在しないのか、閲覧権限がないのかは区別できません',
    );
  });

  // SC-18 主要素 6 / ADR-0049: 打ち切り帯は総数と「絞ることを促す」文言を出す。
  it('shows the truncation banner with the total count', async () => {
    respond({ view: { ...VIEW, truncated: true, totalNodes: 1432, totalEdges: 900 } });
    await renderPage();

    const banner = await screen.findByTestId('truncation-banner');
    expect(banner).toHaveTextContent('上位 3 件を表示（全 1432 件）');
    expect(banner).toHaveTextContent('辺の型を絞って');
    expect(banner).not.toHaveTextContent('件以上');
  });

  // ADR-0049 決定 3・4: 算出用上限に達したら「以上」の形にし、並びが近似であることを明記する。
  it('marks the total as a lower bound when the counting limit was hit', async () => {
    respond({
      view: {
        ...VIEW,
        truncated: true,
        totalNodes: 2000,
        totalEdges: 5000,
        totalIsLowerBound: true,
      },
    });
    await renderPage();

    const banner = await screen.findByTestId('truncation-banner');
    expect(banner).toHaveTextContent('全 2000 件以上');
    expect(banner).toHaveTextContent('厳密な上位 3 件ではありません');
  });

  // 陽性対照: 上限に達していなければ帯は出ない（常時表示の帯は「打ち切りの表示」ではない）。
  it('shows no banner when nothing was truncated', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    expect(screen.queryByTestId('truncation-banner')).not.toBeInTheDocument();
  });

  // SC-18 主要素 3: hops の変更は URL（単一情報源）とサーバ照会の両方へ効く。UI は 1/2/3 しか出さない。
  it('re-queries with the selected hops', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await userEvent.selectOptions(screen.getByLabelText('探索深さ（hops）'), '3');

    await waitFor(() => expect(lastNeighborsPath()).toContain('hops=3'));
  });

  // 🔴 SC-18 主要素 4: 辺の型フィルタは**サーバ側**で適用される（planning#446）——
  // チェックを外すと types パラメータつきで**再照会**する。クライアントで辺を間引かない。
  it('re-queries the server with the types filter when a type is unchecked', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    expect(lastNeighborsPath()).not.toContain('types=');

    await userEvent.click(screen.getByRole('checkbox', { name: 'related' }));

    await waitFor(() => expect(lastNeighborsPath()).toContain('types='));
    const path = lastNeighborsPath();
    expect(path).toContain(TYPE_CITES);
    expect(path).not.toContain(TYPE_RELATED);
  });

  // 全 OFF は作れない（最後の 1 つは disabled）。「何も描かない探索」を要求として送らない。
  it('does not allow unchecking the last remaining type', async () => {
    respond();
    await renderPage(`/graph?root=${ROOT}&types=${TYPE_CITES}`);

    await screen.findByTestId('graph-canvas');
    expect(screen.getByRole('checkbox', { name: 'cites' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'related' })).not.toBeChecked();
  });

  // SC-18 主要素 7: グラフ内検索（表示中ノードのタイトル部分一致・権限内のみが対象）。
  it('finds nodes by title and opens the side panel from a match', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await userEvent.type(screen.getByLabelText('グラフ内検索'), '旅費');

    const results = await screen.findByTestId('node-search-results');
    expect(results).toHaveTextContent('旅費規程');

    await userEvent.click(screen.getByRole('button', { name: '旅費規程' }));
    const panel = await screen.findByTestId('node-side-panel');
    expect(panel).toHaveTextContent('旅費規程');
  });

  it('reports when no node matches the search', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await userEvent.type(screen.getByLabelText('グラフ内検索'), '存在しない語');

    expect(await screen.findByText('該当するノードがありません。')).toBeInTheDocument();
  });

  // SC-18 主要素 5: ノード選択でサイドパネル（タイトル / 種別 / 更新日 / タグ / 接続辺 / 開く導線）。
  it('opens the side panel on node click with detail, edges and the open link', async () => {
    respond();
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await waitFor(() => expect(mocks.clickHandlers.length).toBeGreaterThan(0));
    act(() => {
      mocks.clickHandlers.forEach((h) => h({ dataType: 'node', data: { id: LEAF } }));
    });

    const panel = await screen.findByTestId('node-side-panel');
    expect(panel).toHaveTextContent('旅費規程');
    expect(panel).toHaveTextContent('組織文書');
    // 詳細（更新日・タグ）は選択後に 1 件だけ取りに行くため、1 段階遅れて現れる。
    await waitFor(() => expect(panel).toHaveTextContent('経理'));
    expect(panel).toHaveTextContent('更新日:');
    // 接続辺の一覧は型名で集計される（cites 1）。
    expect(panel).toHaveTextContent('cites: 1');
    const open = screen.getByRole('link', { name: '文書を開く' });
    expect(open).toHaveAttribute('href', `/docs/${LEAF}`);
  });

  // 個人資料は種別ラベルでも区別する（形＋アイコンに加えたテキストの手掛かり）。
  it('labels private notes in the side panel', async () => {
    respond({ detail: { ...DETAIL, id: NOTE, title: '自分の下書きメモ', tags: [] } });
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await waitFor(() => expect(mocks.clickHandlers.length).toBeGreaterThan(0));
    act(() => {
      mocks.clickHandlers.forEach((h) => h({ dataType: 'node', data: { id: NOTE } }));
    });

    const panel = await screen.findByTestId('node-side-panel');
    expect(panel).toHaveTextContent('個人資料（自分のみ）');
  });

  // 文書詳細の 404（不在と権限は区別されない。IADR-0009）はパネルを壊さない。
  it('keeps the panel usable when the detail is hidden', async () => {
    respond({ detail: new ApiError('notFound', '見つかりませんでした。', 404) });
    await renderPage();

    await screen.findByTestId('graph-canvas');
    await waitFor(() => expect(mocks.clickHandlers.length).toBeGreaterThan(0));
    act(() => {
      mocks.clickHandlers.forEach((h) => h({ dataType: 'node', data: { id: LEAF } }));
    });

    const panel = await screen.findByTestId('node-side-panel');
    await waitFor(() => expect(panel).toHaveTextContent('文書の詳細は表示できません。'));
    expect(panel).toHaveTextContent('旅費規程');
  });

  // SC-18 主要素 8（空状態 その 1）: 200 で辺 0 本 → 「関係する文書がありません」。
  it('shows the no-relations empty state', async () => {
    respond({
      view: {
        nodes: [{ documentId: ROOT, title: '孤独な文書', isPrivateNote: false }],
        edges: [],
        truncated: false,
        totalNodes: 1,
        totalEdges: 0,
        totalIsLowerBound: false,
      },
    });
    await renderPage();

    expect(await screen.findByTestId('empty-no-relations')).toHaveTextContent(
      '関係する文書がありません',
    );
    expect(screen.queryByTestId('empty-denied')).not.toBeInTheDocument();
  });

  // SC-18 主要素 8（空状態 その 2）: 404 → 「権限のある文書がありません」。
  // 権限外・不在は同じ 404 で秘匿される（ADR-0034 決定 2）ため、区別できない旨も添える。
  it('shows the denied-or-missing empty state on 404', async () => {
    respond({ view: new ApiError('notFound', '見つかりませんでした。', 404) });
    await renderPage();

    const empty = await screen.findByTestId('empty-denied');
    expect(empty).toHaveTextContent('権限のある文書がありません');
    expect(empty).toHaveTextContent('区別できません');
    expect(screen.queryByTestId('empty-no-relations')).not.toBeInTheDocument();
  });

  // root 未指定は探索前の案内（空状態 2 種とは別）。照会は送らない。
  it('prompts for a root when none is given and does not query', async () => {
    respond();
    await renderPage('/graph');

    expect(await screen.findByText('起点が未指定です')).toBeInTheDocument();
    expect(lastNeighborsPath()).toBeUndefined();
  });

  // URL の不正値（hops=9 等）は既定へ正規化される。サーバの 400 を見せる形にしない。
  it('normalises invalid search params to defaults', async () => {
    respond();
    await renderPage(`/graph?root=${ROOT}&hops=9&by=unknown`);

    await screen.findByTestId('graph-canvas');
    expect(screen.getByLabelText('探索深さ（hops）')).toHaveValue('2');
    expect(screen.getByLabelText('間引きの基準')).toHaveValue('distance');
    expect(lastNeighborsPath()).toContain('hops=2');
  });
});
