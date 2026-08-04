import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-02, UC-01（代替フロー）, FR-03/FR-05: 検索結果一覧の再実装（#502）。
// 検索語は URL（?q=）が単一情報源であり（IADR-0126 決定 3）、入力欄は取得の引き金にならない。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiFetch: mocks.apiFetch,
}));

import { createSc02ResultsRoute } from './index';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const RESPONSE = {
  results: [
    {
      chunkId: 'c1',
      documentId: DOC_ID,
      documentTitle: '経費精算規程 v3.2',
      text: '…精算の締め日は毎月25日とし…',
      score: 0.91,
      attributes: { confidentiality: 'internal' },
      tags: ['経理', '規程'],
    },
  ],
  totalHits: 24,
  elapsedMs: 5,
};

async function renderPage(initialPath = '/search') {
  return renderUnitRoute((shell) => [createSc02ResultsRoute(shell)], { initialEntry: initialPath });
}

async function search(term: string) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('キーワード・意味検索'), term);
  await user.click(screen.getByRole('button', { name: '検索' }));
  return user;
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('SearchResultsPage (SC-02)', () => {
  // UC-01 代替フロー: キーワード検索のみで結果一覧を返す（AI 回答は呼ばない）。
  // UC-01 基本フロー 2 / FR-05: クライアントは ABAC スコープを送らない。
  it('searches via /bff/search and lists results linking to SC-03', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    await renderPage();
    await search('経費精算');

    const link = await screen.findByRole('link', { name: '経費精算規程 v3.2' });
    expect(link).toHaveAttribute('href', `/docs/${DOC_ID}`);
    expect(screen.getByText(/精算の締め日は毎月25日/)).toBeInTheDocument();
    expect(screen.getByText('経理')).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/search', {
      method: 'POST',
      json: { query: '経費精算', topK: 20 },
    });
    // AI 回答（/analysis/ask/stream）は呼ばない＝この画面は代替フローだけを担う。
    expect(mocks.apiFetch.mock.calls.every(([path]) => path === '/search')).toBe(true);
  });

  // FR-05: 一覧が全体ではないことを明示する（05_screens §SC-02「権限内のみ表示」）。
  it('states that only permitted documents are listed', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    await renderPage();
    await search('経費精算');

    expect(await screen.findByText(/24 件（権限内のみ表示）/)).toBeInTheDocument();
    // 総数（24）> 表示件数（1）のときは、表示している件数も示す。
    expect(screen.getByText(/（表示 1 件）/)).toBeInTheDocument();
  });

  // IADR-0126 決定 3: `?q=` は検索語の単一情報源。ディープリンク・戻る操作でそのまま再現される。
  it('auto-searches from the ?q= deep link', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    await renderPage('/search?q=%E7%B5%8C%E8%B2%BB');

    expect(await screen.findByRole('link', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/search', {
      method: 'POST',
      json: { query: '経費', topK: 20 },
    });
  });

  // IADR-0126 決定 3: 発火点は URL の 1 つだけ。送信 1 回で要求は 1 回である
  // （旧実装は「送信の直接実行」と「?q= 変化の useEffect」で二重発火し、ガードで抑えていた）。
  it('fires exactly one request per submission (URL is the single source of truth)', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    await renderPage();
    await search('経費精算');

    await screen.findByRole('link', { name: '経費精算規程 v3.2' });
    expect(mocks.apiFetch).toHaveBeenCalledTimes(1);
  });

  // deny-by-default: 権限外・0 件はいずれも中立に表示する（存在秘匿・IADR-0009）。
  it('shows a neutral empty message when results are empty (existence hidden)', async () => {
    mocks.apiFetch.mockResolvedValue({ results: [], totalHits: 0, elapsedMs: 1 });
    await renderPage();
    await search('取締役会');

    expect(await screen.findByText('該当する文書が見つかりませんでした。')).toBeInTheDocument();
    // 「権限がない」を示唆する文言を出さない。
    expect(screen.queryByText(/権限がありません/)).not.toBeInTheDocument();
  });

  it('shows an alert when the search request fails', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('server', 'サーバでエラーが発生しました。', 500));
    await renderPage();
    await search('x');

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
  });

  // 空クエリでは要求を出さない（`enabled: false`）。
  it('does not request anything without a query', async () => {
    await renderPage();

    expect(mocks.apiFetch).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: '検索' })).toBeDisabled();
  });

  // 導線: SC-01 へ戻れる。
  it('links back to SC-01', async () => {
    await renderPage();
    expect(screen.getByRole('link', { name: '← チャットに戻る' })).toHaveAttribute('href', '/ask');
  });

  it('renders in English when the en locale is active', async () => {
    activate('en');
    await renderPage();

    expect(screen.getByRole('heading', { name: 'Search results' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Search' })).toBeInTheDocument();
  });
});
