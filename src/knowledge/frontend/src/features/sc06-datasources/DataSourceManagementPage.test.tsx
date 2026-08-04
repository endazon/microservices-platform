import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-06, FR-01/FR-02: データソース管理が一覧・登録・同期・無効化を BFF 経由で行うこと、
// 登録ペイロードに既定機密区分（ABAC 属性）を含むこと、異常系を検証する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch }));

import { createSc06DataSourcesRoute } from './index';
import { createSc07ConversionsRoute } from '../sc07-conversions';

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

// ADR-0031 / IADR-0124: 実ルート定義（RequireRole 込み）の上で描画する。SC-07 への導線を解決するため
// 変換ジョブのルートも同居させる。
async function renderPage() {
  return renderUnitRoute(
    (shell) => [createSc06DataSourcesRoute(shell), createSc07ConversionsRoute(shell)],
    { initialEntry: '/admin/sources', roles: ['platform-admin'] },
  );
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

describe('DataSourceManagementPage (SC-06)', () => {
  it('lists registered data sources', async () => {
    mocks.apiFetch.mockResolvedValue(SOURCES);
    await renderPage();

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
    await renderPage();

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
    await renderPage();

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
    await renderPage();

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
    await renderPage();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('取得に失敗'));
  });

  it('notifies when a sync trigger fails', async () => {
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // load
    mocks.apiFetch.mockRejectedValueOnce(new Error('boom')); // sync fails
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '同期' }));

    expect(await screen.findByText('同期のトリガに失敗しました。')).toBeInTheDocument();
  });

  it('notifies when disabling fails', async () => {
    mocks.apiFetch.mockResolvedValueOnce(SOURCES); // load
    mocks.apiFetch.mockRejectedValueOnce(new Error('boom')); // delete fails
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '無効化' }));

    expect(await screen.findByText('無効化に失敗しました。')).toBeInTheDocument();
  });

  it('shows an alert when registration fails', async () => {
    mocks.apiFetch.mockResolvedValueOnce([]); // load
    mocks.apiFetch.mockRejectedValueOnce(new Error('boom')); // create fails
    const user = userEvent.setup();
    await renderPage();

    const form = await screen.findByRole('form', { name: 'データソース登録' });
    await user.type(within(form).getByLabelText('名前（必須）'), '新ソース');
    await user.type(within(form).getByLabelText('接続先 URI（必須）'), 'smb://x/y');
    await user.click(within(form).getByRole('button', { name: '登録する' }));

    expect(await within(form).findByRole('alert')).toHaveTextContent('登録に失敗');
  });
});
