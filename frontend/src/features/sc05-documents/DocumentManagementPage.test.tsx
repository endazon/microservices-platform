import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { ApiError } from '@foundation/api/ApiError';

// SC-05, FR-06: 文書管理が一覧・作成・編集（楽観ロック）・公開・削除を BFF 経由で行うこと、
// 必須属性（機密区分）を含むペイロード、版競合（409）の通知・再読込、異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { DocumentManagementPage } from './DocumentManagementPage';

const ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const DOCS = [
  { id: ID, title: '経費規程 2025', status: 'draft', version: 3, attributes: { confidentiality: 'internal' }, tags: ['hr'], updatedAt: '2026-07-01T00:00:00Z' },
];

function renderPage() {
  return render(
    <MemoryRouter>
      <DocumentManagementPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('DocumentManagementPage (SC-05)', () => {
  it('lists documents linking to SC-03 detail', async () => {
    mocks.apiFetch.mockResolvedValue(DOCS);
    renderPage();

    const link = await screen.findByRole('link', { name: '経費規程 2025' });
    expect(link).toHaveAttribute('href', `/documents/${ID}`);
    expect(screen.getByText('v3')).toBeInTheDocument();
  });

  it('creates a document with the required confidentiality attribute', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce({ id: 'new' }); // create
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    const form = screen.getByRole('form', { name: '文書作成' });
    await user.type(within(form).getByLabelText('タイトル（必須）'), '新規文書');
    await user.selectOptions(within(form).getByLabelText('機密区分（必須）'), 'confidential');
    await user.type(within(form).getByLabelText('タグ（カンマ区切り）'), 'hr, legal');
    await user.click(within(form).getByRole('button', { name: '作成する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/documents', {
        method: 'POST',
        json: { title: '新規文書', attributes: { confidentiality: 'confidential' }, tags: ['hr', 'legal'] },
      }),
    );
  });

  it('publishes a draft document', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce(undefined); // publish
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '公開' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(`/documents/${ID}/publish`, { method: 'POST' }),
    );
  });

  it('edits a document with optimistic concurrency (expectedVersion)', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockResolvedValueOnce({ id: ID }); // put
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '編集' }));
    const form = screen.getByRole('form', { name: '文書編集' });
    await user.clear(within(form).getByLabelText('タイトル（必須）'));
    await user.type(within(form).getByLabelText('タイトル（必須）'), '経費規程 2025 改訂');
    await user.click(within(form).getByRole('button', { name: '保存する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(
        `/documents/${ID}`,
        expect.objectContaining({ method: 'PUT', json: expect.objectContaining({ expectedVersion: 3, title: '経費規程 2025 改訂' }) }),
      ),
    );
  });

  it('shows a conflict notice and reloads on 409 version conflict', async () => {
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // load
    mocks.apiFetch.mockRejectedValueOnce(new ApiError('unknown', '要求が失敗しました（409）。', 409)); // put -> conflict
    mocks.apiFetch.mockResolvedValueOnce(DOCS); // reload
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('link', { name: '経費規程 2025' });

    await user.click(screen.getByRole('button', { name: '編集' }));
    await user.click(within(screen.getByRole('form', { name: '文書編集' })).getByRole('button', { name: '保存する' }));

    expect(await screen.findByText(/競合しました/)).toBeInTheDocument();
  });

  it('shows an alert when the list fails to load', async () => {
    mocks.apiFetch.mockRejectedValue(new Error('boom'));
    renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗'));
  });
});
