import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';
import type {
  WikiPageSummary,
  WikiPageView,
  WikiSearchHit,
} from '@foundation/api/generated/bff.schemas';

// SC-04, UC-07, FR-13, FR-05, ADR-0073 決定 1・2・4 / IADR-0365 (#1200): Wiki 閲覧画面。
// ページツリー・本文・検索を `/bff/wiki/*` 経由で描くこと、存在秘匿（404 と空）を中立に描くこと、
// 故障（502）を空で隠さないこと、外部リンクが 1 本も無いことを固定する。
//
// IADR-0135 決定 4: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、モックは `apiRequest` に当てる。
const mocks = vi.hoisted(() => ({
  apiRequest: vi.fn(),
  // 05_screens §共通シェル / #446: パンくずの動的な葉。渡している値そのものを見る。
  useBreadcrumbLeaf: vi.fn(),
}));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));
vi.mock('@foundation/routing/breadcrumbLeaf', () => ({
  useBreadcrumbLeaf: mocks.useBreadcrumbLeaf,
}));

import { createSc04WikiRoute } from '../routes/sc04WikiRoute';

const DOC_A = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const DOC_B = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

const PAGE_A: WikiPageSummary = {
  id: 'page-a',
  documentId: DOC_A,
  title: '経費精算規程',
  slug: 'keihi-seisan',
  wikiPath: `doc/${DOC_A}`,
  status: 'Active',
  syncedAt: '2026-09-01T00:00:00Z',
};
const PAGE_B: WikiPageSummary = {
  ...PAGE_A,
  id: 'page-b',
  documentId: DOC_B,
  title: '旅費規程',
  slug: 'ryohi',
  wikiPath: `doc/${DOC_B}`,
};
// 🔴 本文は **Wiki.js が描画した HTML** である。見出し・強調（残る側）と、スクリプト・画像・
// 新規タブ属性（落ちる側）、ページ間リンク（書き換わる側）を 1 本に入れて対で見る。
const VIEW_A: WikiPageView = {
  ...PAGE_A,
  content:
    '<h2>申請の手順</h2><p><b>領収書</b>を添付する。</p>' +
    '<script>alert(1)</script><img src="https://evil.example/x.png" alt="x">' +
    `<a href="/ja/doc/${DOC_B}" target="_blank">旅費規程を見る</a>`,
};
const HIT_B: WikiSearchHit = {
  id: PAGE_B.id,
  documentId: DOC_B,
  title: PAGE_B.title,
  slug: PAGE_B.slug,
  wikiPath: PAGE_B.wikiPath,
  syncedAt: PAGE_B.syncedAt,
};

/** BFF の各端点へ応答を割り当てる（既定はすべて成功）。`Error` を渡すとその端点だけ失敗する。 */
function respond({
  pages = [PAGE_A, PAGE_B] as unknown,
  page = VIEW_A as unknown,
  byDoc = VIEW_A as unknown,
  hits = [HIT_B] as unknown,
}: { pages?: unknown; page?: unknown; byDoc?: unknown; hits?: unknown } = {}) {
  const reply = (value: unknown) =>
    value instanceof Error ? Promise.reject(value) : Promise.resolve(jsonResponse(value));
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path === '/wiki/pages') return reply(pages);
    if (path.startsWith('/wiki/search')) return reply(hits);
    if (path.startsWith('/wiki/pages/by-doc/')) return reply(byDoc);
    if (path.startsWith('/wiki/pages/')) return reply(page);
    return Promise.reject(new Error(`unexpected path: ${path}`));
  });
}

const calledPaths = () => mocks.apiRequest.mock.calls.map((c) => String(c[0]));

async function renderPage(initialEntry = '/wiki') {
  return renderUnitRoute((shell) => [createSc04WikiRoute(shell)], { initialEntry });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.useBreadcrumbLeaf.mockClear();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('WikiBrowsePage (SC-04) — page tree', () => {
  // UC-07 基本フロー 2 / FR-05: 権限内のページだけが並ぶ（絞りは後段。画面は届いた一覧をそのまま描く）。
  it('renders the permitted pages as a tree of links keyed by slug and fetches no body yet', async () => {
    respond();
    await renderPage();

    const tree = await screen.findByRole('navigation', { name: 'ページツリー' });
    const links = within(tree).getAllByRole('link');
    expect(links.map((l) => l.textContent)).toEqual(['経費精算規程', '旅費規程']);
    expect(links[0]).toHaveAttribute('href', '/wiki?page=keihi-seisan');
    // 何も選んでいないので本文は取りに行かない。
    expect(screen.getByRole('note')).toHaveTextContent('ページを選んでください');
    expect(calledPaths().filter((p) => p.startsWith('/wiki/pages/'))).toEqual([]);
    // 見出しは静的な画面名（パンくずの葉はまだ無い）。
    expect(screen.getByRole('heading', { name: 'Wiki 閲覧', level: 1 })).toBeInTheDocument();
  });

  // UC-07 例外フロー / IADR-0009: deny-by-default の空は「権限が無い」と言わず、中立に描く。
  it('shows a neutral message when the ledger is empty (deny-by-default is not distinguished)', async () => {
    respond({ pages: [] });
    await renderPage();

    expect(await screen.findByText('閲覧できる Wiki ページはありません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // IADR-0355 決定 5: 502 は空で隠さない（「壊れている」は「無い」と別の軸）。
  it('shows an alert (not the empty text) when the ledger cannot be fetched', async () => {
    respond({ pages: ApiError.fromStatus(502) });
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'ページツリーを取得できませんでした',
    );
    expect(screen.queryByText('閲覧できる Wiki ページはありません。')).not.toBeInTheDocument();
  });
});

describe('WikiBrowsePage (SC-04) — page body', () => {
  // UC-07 基本フロー 1「開く」/ ADR-0073 決定 2: 本文は Wiki.js が描画した HTML をそのまま（sanitize して）描く。
  it('opens a page by slug and renders the Wiki.js HTML sanitized', async () => {
    respond();
    await renderPage('/wiki?page=keihi-seisan');

    // ★ 陽性対照: Wiki.js の見出しが**見出しとして**描かれる（SC-03 が Markdown 原文を等幅で出すのと対照的）。
    expect(
      await screen.findByRole('heading', { name: '申請の手順', level: 2 }),
    ).toBeInTheDocument();
    const article = screen.getByTestId('wiki-page-content');
    expect(article.querySelector('b')).toHaveTextContent('領収書');
    // ★ 陰性対照: スクリプト・画像・新規タブ属性は落ちる（IADR-0365 決定 3）。
    expect(article.querySelector('script')).toBeNull();
    expect(article.querySelector('img')).toBeNull();
    expect(article.querySelector('a[target]')).toBeNull();
    // ページ間リンクは SPA 側の到達先へ書き換わる。
    expect(within(article).getByRole('link', { name: '旅費規程を見る' })).toHaveAttribute(
      'href',
      `/wiki?doc=${DOC_B}`,
    );
    // 取得はスラッグの経路で行う。
    expect(calledPaths()).toContain('/wiki/pages/keihi-seisan');
    expect(calledPaths().filter((p) => p.includes('/by-doc/'))).toEqual([]);
    // 題名・最終同期・SC-03 への復帰リンク（05_screens §SC-04 §主要素）。
    expect(screen.getByRole('heading', { name: '経費精算規程', level: 2 })).toBeInTheDocument();
    expect(screen.getByText(/最終同期:/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '文書詳細へ戻る' })).toHaveAttribute(
      'href',
      `/docs/${DOC_A}`,
    );
    // 開いているページはツリーで現在地になる。
    const tree = screen.getByRole('navigation', { name: 'ページツリー' });
    expect(within(tree).getByRole('link', { name: '経費精算規程' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    // パンくずの葉はページの題名。
    await waitFor(() => expect(mocks.useBreadcrumbLeaf).toHaveBeenCalledWith('経費精算規程'));
  });

  // #1200: 文書別ディープリンク（SC-01 の出典・SC-03 の「Wiki で閲覧」が使う）。
  it('opens a page by document id through the by-doc route', async () => {
    respond();
    await renderPage(`/wiki?doc=${DOC_A}`);

    expect(
      await screen.findByRole('heading', { name: '申請の手順', level: 2 }),
    ).toBeInTheDocument();
    expect(calledPaths()).toContain(`/wiki/pages/by-doc/${DOC_A}`);
    expect(calledPaths()).not.toContain('/wiki/pages/keihi-seisan');
  });

  it('prefers the slug when both page and doc are present', async () => {
    respond();
    await renderPage(`/wiki?page=keihi-seisan&doc=${DOC_B}`);

    await screen.findByRole('heading', { name: '申請の手順', level: 2 });
    expect(calledPaths()).toContain('/wiki/pages/keihi-seisan');
    expect(calledPaths().filter((p) => p.includes('/by-doc/'))).toEqual([]);
  });

  // UC-07 例外フロー / IADR-0009: 権限外・不存在・アーカイブ済みは同じ 404。中立に描き、alert にしない。
  it('shows a neutral not-found message on 404 (existence hidden)', async () => {
    respond({ page: ApiError.fromStatus(404) });
    await renderPage('/wiki?page=secret');

    expect(await screen.findByText('ページが見つかりませんでした。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    // ★ 陽性対照: ツリーは変わらず描かれる（一覧と本文で門は同じだが、画面は片方の失敗で全体を消さない）。
    expect(await screen.findByRole('navigation', { name: 'ページツリー' })).toBeInTheDocument();
  });

  it('shows an alert (not the not-found text) when the body fails with 5xx', async () => {
    respond({ page: ApiError.fromStatus(502) });
    await renderPage('/wiki?page=keihi-seisan');

    expect(await screen.findByRole('alert')).toHaveTextContent('本文を取得できませんでした');
    expect(screen.queryByText('ページが見つかりませんでした。')).not.toBeInTheDocument();
  });

  // #1200 受け入れ基準: Wiki.js 本体 UI への外部リンクは 1 本も無い（DOM 側の対照。ソースの grep と対）。
  it('renders no link that opens a new tab', async () => {
    respond();
    await renderPage('/wiki?page=keihi-seisan');

    await screen.findByRole('heading', { name: '申請の手順', level: 2 });
    expect(document.querySelectorAll('a[target="_blank"]')).toHaveLength(0);
  });
});

describe('WikiBrowsePage (SC-04) — search', () => {
  // UC-07 基本フロー 1「検索する」/ 05_screens §SC-04 §アクション「検索語で権限内のページを絞り込む」。
  it('searches from the box, puts the term in the url and lists the hits as links', async () => {
    respond();
    await renderPage();
    const user = userEvent.setup();

    // 問う前に検索の口を叩かない。
    await screen.findByRole('navigation', { name: 'ページツリー' });
    expect(calledPaths().filter((p) => p.startsWith('/wiki/search'))).toEqual([]);

    await user.type(screen.getByLabelText('Wiki を検索'), '旅費');
    await user.click(screen.getByRole('button', { name: '検索' }));

    const results = await screen.findByRole('list', { name: '検索結果' });
    expect(within(results).getByRole('link', { name: '旅費規程' }).getAttribute('href')).toContain(
      'page=ryohi',
    );
    expect(calledPaths()).toContain(`/wiki/search?q=${encodeURIComponent('旅費')}`);
  });

  it('does not search on a blank term', async () => {
    respond();
    await renderPage();
    const user = userEvent.setup();

    await screen.findByRole('navigation', { name: 'ページツリー' });
    await user.type(screen.getByLabelText('Wiki を検索'), '   ');
    await user.click(screen.getByRole('button', { name: '検索' }));

    await waitFor(() => expect(mocks.apiRequest).toHaveBeenCalled());
    expect(calledPaths().filter((p) => p.startsWith('/wiki/search'))).toEqual([]);
  });

  it('shows a neutral message when nothing matched', async () => {
    respond({ hits: [] });
    await renderPage('/wiki?q=x');

    expect(await screen.findByText('該当するページはありません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // IADR-0355 決定 5 / IADR-0335 決定 2: Wiki.js 不達の 502 は空で隠さない。
  it('shows an alert when the search backend is unreachable (502 is not hidden as empty)', async () => {
    respond({ hits: ApiError.fromStatus(502) });
    await renderPage('/wiki?q=x');

    expect(await screen.findByRole('alert')).toHaveTextContent('Wiki の検索に失敗しました');
    expect(screen.queryByText('該当するページはありません。')).not.toBeInTheDocument();
  });
});

describe('WikiBrowsePage (SC-04) — i18n', () => {
  // ADR-0031（i18n = Lingui〔ja / en〕）: 生の日本語文字列へ戻す退行はここが落とす。
  it('renders in English when the en locale is active', async () => {
    respond();
    activate('en');
    await renderPage();

    expect(
      await screen.findByRole('heading', { name: 'Browse the wiki', level: 1 }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Search' })).toBeInTheDocument();
    expect(await screen.findByRole('navigation', { name: 'Page tree' })).toBeInTheDocument();
  });
});
