import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

// SC-06, FR-01/FR-02: データソース管理が一覧・登録・同期・無効化を BFF 経由で行うこと、
// 登録ペイロードに既定機密区分（ABAC 属性）を含むこと、異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { DataSourceManagementPage } from './DataSourceManagementPage';

const SOURCES = [
  {
    id: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
    name: '社内共有フォルダ',
    sourceType: 'filesystem',
    connectionUri: 'smb://share/docs',
    status: 'active',
    lastSyncedAt: '2026-07-08T00:00:00Z',
    defaultAttributes: { confidentiality: 'internal' },
    createdAt: '2026-07-01T00:00:00Z',
  },
];

function renderPage() {
  return render(
    <MemoryRouter>
      <DataSourceManagementPage />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('DataSourceManagementPage (SC-06)', () => {
  it('lists registered data sources', async () => {
    mocks.apiFetch.mockResolvedValue(SOURCES);
    renderPage();

    expect(await screen.findByText('社内共有フォルダ')).toBeInTheDocument();
    expect(screen.getByText('smb://share/docs')).toBeInTheDocument();
    // 既定機密区分（ABAC 属性）がテーブルセルに表示される（登録フォームの option とは区別する）。
    expect(screen.getByRole('cell', { name: 'internal' })).toBeInTheDocument();
  });

  it('creates a data source with the default confidentiality attribute', async () => {
    // 初回一覧 → 登録 → 再一覧。
    mocks.apiFetch.mockResolvedValueOnce([]); // load
    mocks.apiFetch.mockResolvedValueOnce({ id: 'x' }); // create
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // reload
    const user = userEvent.setup();
    renderPage();

    const form = await screen.findByRole('form', { name: 'データソース登録' });
    await user.type(within(form).getByLabelText('名前（必須）'), '新ソース');
    await user.type(within(form).getByLabelText('接続先 URI（必須）'), 'smb://x/y');
    await user.click(within(form).getByRole('button', { name: '登録する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/datasources', {
        method: 'POST',
        json: {
          name: '新ソース',
          sourceType: 'filesystem',
          connectionUri: 'smb://x/y',
          defaultAttributes: { confidentiality: 'internal' },
        },
      }),
    );
  });

  it('triggers a manual sync', async () => {
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // load
    mocks.apiFetch.mockResolvedValueOnce(undefined); // sync
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // reload
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: '同期' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(
        '/datasources/cccccccc-cccc-cccc-cccc-cccccccccccc/sync',
        { method: 'POST' },
      ),
    );
  });

  it('disables a data source', async () => {
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // load
    mocks.apiFetch.mockResolvedValueOnce(undefined); // delete
    mocks.apiFetch.mockResolvedValueOnce([]); // reload
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: '無効化' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(
        '/datasources/cccccccc-cccc-cccc-cccc-cccccccccccc',
        { method: 'DELETE' },
      ),
    );
  });

  it('shows an alert when the list fails to load', async () => {
    mocks.apiFetch.mockRejectedValue(new Error('boom'));
    renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗'));
  });
});
