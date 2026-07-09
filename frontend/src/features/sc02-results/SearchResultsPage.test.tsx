import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

// SC-02, FR-03/FR-05: 検索結果一覧が /bff/search を呼び、結果を一覧表示し SC-03 へ内部リンクすること、
// deny-by-default（空一覧）を中立に表示すること、?q= ディープリンクで自動検索することを検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { SearchResultsPage } from './SearchResultsPage';

const RESPONSE = {
  results: [
    {
      chunkId: 'c1',
      documentId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      documentTitle: '経費規程 2025',
      text: '第3条 出張旅費の上限を…',
      score: 0.91,
      attributes: { confidentiality: 'internal' },
      tags: ['hr'],
    },
  ],
  totalHits: 1,
  elapsedMs: 5,
};

function renderPage(initialPath = '/results') {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route path="results" element={<SearchResultsPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

// 注意: ブロック本体で undefined を返す（mockReset の戻り値を暗黙 return しない）。
beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('SearchResultsPage (SC-02)', () => {
  it('searches on submit and lists results linking to SC-03 document detail', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText('キーワード・意味検索'), '経費');
    await user.click(screen.getByRole('button', { name: '検索する' }));

    const link = await screen.findByRole('link', { name: '経費規程 2025' });
    expect(link).toHaveAttribute('href', '/documents/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
    expect(screen.getByText(/第3条 出張旅費/)).toBeInTheDocument();
    expect(screen.getByText(/confidentiality: internal/)).toBeInTheDocument();
    // /bff/search を POST で呼ぶ（クライアントは ABAC スコープを送らない）。
    expect(mocks.apiFetch).toHaveBeenCalledWith('/search', { method: 'POST', json: { query: '経費', topK: 20 } });
    // 送信 1 回につき検索は 1 回だけ（setParams による ?q= 更新で二重発火しない・レビュー #168 指摘対応）。
    expect(mocks.apiFetch).toHaveBeenCalledTimes(1);
  });

  it('does not double-fire the search when submitting (single trigger path)', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText('キーワード・意味検索'), '経費');
    await user.click(screen.getByRole('button', { name: '検索する' }));
    await screen.findByRole('link', { name: '経費規程 2025' });

    // onSubmit の直接実行と ?q= 変化 useEffect が二重に走らないこと。
    expect(mocks.apiFetch).toHaveBeenCalledTimes(1);
  });

  it('auto-searches from the ?q= deep link', async () => {
    mocks.apiFetch.mockResolvedValue(RESPONSE);
    renderPage('/results?q=%E7%B5%8C%E8%B2%BB');

    expect(await screen.findByRole('link', { name: '経費規程 2025' })).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/search', { method: 'POST', json: { query: '経費', topK: 20 } });
  });

  it('shows a neutral empty message when access-scoped results are empty (existence hidden)', async () => {
    mocks.apiFetch.mockResolvedValue({ results: [], totalHits: 0, elapsedMs: 1 });
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText('キーワード・意味検索'), '取締役会');
    await user.click(screen.getByRole('button', { name: '検索する' }));

    expect(await screen.findByText('該当する文書が見つかりませんでした。')).toBeInTheDocument();
  });

  it('shows an alert when the search request fails', async () => {
    mocks.apiFetch.mockRejectedValue(new Error('boom'));
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText('キーワード・意味検索'), 'x');
    await user.click(screen.getByRole('button', { name: '検索する' }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('検索に失敗'));
  });
});
