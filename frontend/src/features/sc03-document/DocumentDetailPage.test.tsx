import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { ApiError } from '@foundation/api/ApiError';

// SC-03, FR-06/FR-12: 文書詳細（メタデータ＋本文＋版履歴）の表示、本文領域の独立縮退、
// 404 の中立表示（存在秘匿）、異常系を検証する。データソース /documents/:id・/content・/versions を
// パスで振り分けてモックする。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));
// Wiki 導線の表示条件（wikiBaseUrl 設定）を満たすため config をスタブする。
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ bffBaseUrl: '/bff', oidc: { authority: 'x', clientId: 'y' }, opsLinks: {}, wikiBaseUrl: 'https://wiki.example' }),
}));

import { DocumentDetailPage } from './DocumentDetailPage';

const ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const DOC = {
  id: ID,
  title: '経費規程 2025',
  status: 'published',
  markdownUri: 'storage://bucket/expense.md',
  version: 3,
  attributes: { confidentiality: 'internal' },
  tags: ['hr'],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-07-01T00:00:00Z',
};
const CONTENT = { id: ID, title: '経費規程 2025', markdown: '# 経費規程\n\n第3条 …', sourceUri: 'storage://bucket/expense.md' };
const VERSIONS = [
  { documentId: ID, version: 3, title: '経費規程 2025', status: 'published', changeNote: '第3条改定', createdAt: '2026-07-01T00:00:00Z' },
  { documentId: ID, version: 2, title: '経費規程 2025', status: 'published', changeNote: null, createdAt: '2026-06-01T00:00:00Z' },
];

interface RouteOpts {
  detailError?: ApiError;
  contentError?: boolean;
}
function route(opts: RouteOpts = {}) {
  mocks.apiFetch.mockImplementation(async (path: string) => {
    if (path === `/documents/${ID}`) {
      if (opts.detailError) throw opts.detailError;
      return DOC;
    }
    if (path === `/documents/${ID}/content`) {
      if (opts.contentError) throw new ApiError('server', 'x', 500);
      return CONTENT;
    }
    if (path === `/documents/${ID}/versions`) return VERSIONS;
    throw new ApiError('unknown', 'x', 0);
  });
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={[`/documents/${ID}`]}>
      <Routes>
        <Route path="documents/:id" element={<DocumentDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

// 注意: ブロック本体で undefined を返す（mockReset の戻り値を暗黙 return しない）。
beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('DocumentDetailPage (SC-03)', () => {
  it('renders metadata, markdown body, source link and version history', async () => {
    route();
    renderPage();

    expect(await screen.findByRole('heading', { name: '経費規程 2025' })).toBeInTheDocument();
    expect(screen.getByText(/状態: published/)).toBeInTheDocument();
    expect(screen.getByText(/confidentiality: internal/)).toBeInTheDocument();
    expect(screen.getByText('#hr')).toBeInTheDocument();
    // 本文（Markdown 原文）が表示される。
    expect(await screen.findByText(/第3条 …/)).toBeInTheDocument();
    // 版履歴（v3/v2）。
    expect(screen.getByText('v3')).toBeInTheDocument();
    expect(screen.getByText('第3条改定')).toBeInTheDocument();
  });

  it('shows the SC-04 Wiki navigation link when wikiBaseUrl is configured', async () => {
    route();
    renderPage();
    const link = await screen.findByRole('link', { name: 'Wiki で開く' });
    expect(link).toHaveAttribute('href', '/wiki');
  });

  it('shows a neutral message on 404 (existence hidden)', async () => {
    route({ detailError: new ApiError('notFound', 'x', 404) });
    renderPage();
    expect(await screen.findByText('文書が見つかりませんでした。')).toBeInTheDocument();
  });

  it('shows an alert on server error', async () => {
    route({ detailError: new ApiError('server', 'x', 500) });
    renderPage();
    expect(await screen.findByRole('alert')).toHaveTextContent('取得に失敗');
  });

  it('degrades only the content area when content fetch fails (metadata still shown)', async () => {
    route({ contentError: true });
    renderPage();
    expect(await screen.findByRole('heading', { name: '経費規程 2025' })).toBeInTheDocument();
    expect(await screen.findByText('本文は利用できません。')).toBeInTheDocument();
  });
});
