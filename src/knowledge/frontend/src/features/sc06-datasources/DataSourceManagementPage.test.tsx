import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-06, UC-04, FR-01/FR-02: データソース管理画面の再実装（#503）。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiFetch: mocks.apiFetch,
}));

import { createSc06DataSourcesRoute } from './index';

const ACTIVE_SOURCE = {
  id: '11111111-1111-1111-1111-111111111111',
  name: '規程集',
  sourceType: 'filesystem',
  connectionUri: 'smb://fs01/share/規程集',
  status: 'active',
  lastSyncedAt: '2026-07-24T03:00:00Z',
  config: {},
  defaultAttributes: { confidentiality: 'internal' },
  createdAt: '2026-07-01T00:00:00Z',
};
const DISABLED_SOURCE = {
  ...ACTIVE_SOURCE,
  id: '22222222-2222-2222-2222-222222222222',
  name: '勤怠SaaS API',
  sourceType: 'saas',
  status: 'disabled',
  lastSyncedAt: null,
};

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc06DataSourcesRoute(shell)], {
    initialEntry: '/admin/sources',
    roles,
  });
}

beforeEach(() => {
  mocks.apiFetch.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('DataSourceManagementPage (SC-06)', () => {
  // UC-04: 登録済みソースの一覧・種別・同期状態を確認する。
  // INDEX 決定 21: 同期状態は色だけで意味を持たせない（アイコン ＋ テキストを伴う）。
  it('lists sources with their type and derived sync state', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE, DISABLED_SOURCE]);
    await renderPage();

    expect(await screen.findByText('規程集')).toBeInTheDocument();
    const table = within(screen.getByRole('table'));
    expect(table.getByText('ファイルサーバー')).toBeInTheDocument();
    expect(table.getByText('SaaS')).toBeInTheDocument();
    // active ＋ 最終同期あり → 同期済み。disabled → 無効（琥珀の警告）。
    expect(table.getByText('同期済み')).toBeInTheDocument();
    expect(table.getByText('無効')).toBeInTheDocument();
    expect(mocks.apiFetch).toHaveBeenCalledWith('/datasources');
  });

  // UC-04 基本フロー 1: 管理者がソース（ファイルサーバー／Wiki／SaaS／業務DB）を登録する。
  it('registers a data source with a default confidentiality attribute', async () => {
    mocks.apiFetch.mockResolvedValue([]);
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
    await user.click(screen.getByRole('button', { name: '登録する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/datasources', {
        method: 'POST',
        json: {
          name: '規程集',
          sourceType: 'filesystem',
          connectionUri: 'smb://fs01/share',
          defaultAttributes: { confidentiality: 'internal' },
        },
      }),
    );
    expect(await screen.findByText('データソースを登録しました。')).toBeInTheDocument();
  });

  // 必須（名前・接続先）が埋まるまで登録できない。
  it('keeps the register button disabled until the required fields are filled', async () => {
    mocks.apiFetch.mockResolvedValue([]);
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    expect(screen.getByRole('button', { name: '登録する' })).toBeDisabled();

    await user.type(screen.getByLabelText(/名前/), '規程集');
    expect(screen.getByRole('button', { name: '登録する' })).toBeDisabled();

    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
    expect(screen.getByRole('button', { name: '登録する' })).toBeEnabled();
  });

  // UC-04 代替フロー: 手動同期を実行する。
  it('triggers a manual sync', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE]);
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(`/datasources/${ACTIVE_SOURCE.id}/sync`, {
        method: 'POST',
      }),
    );
    expect(await screen.findByText('同期をトリガしました。')).toBeInTheDocument();
  });

  // IADR-0127 決定 5: 操作の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
  // これが外れると、同期をトリガしたのに最終同期日時が古いまま残る。
  it('refetches the list after a successful sync', async () => {
    mocks.apiFetch.mockImplementation((path: string) =>
      path.endsWith('/sync') ? Promise.resolve(undefined) : Promise.resolve([ACTIVE_SOURCE]),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(mocks.apiFetch.mock.calls.filter(([path]) => path === '/datasources')).toHaveLength(2),
    );
  });

  it('disables an active source and offers no disable action for a disabled one', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE, DISABLED_SOURCE]);
    const user = userEvent.setup();
    await renderPage();

    // 無効化は active の行だけに出る（既に無効なソースへ再度送らない）。
    const buttons = await screen.findAllByRole('button', { name: '無効化' });
    expect(buttons).toHaveLength(1);

    await user.click(buttons[0]);
    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith(`/datasources/${ACTIVE_SOURCE.id}`, {
        method: 'DELETE',
      }),
    );
  });

  // UC-04 例外フロー（接続の継続失敗はアラート）に対応する静的な注記。
  it('states that credentials live in Vault and that repeated failures raise an alert', async () => {
    mocks.apiFetch.mockResolvedValue([]);
    await renderPage();

    expect(
      await screen.findByText(/接続情報（認証情報）は Vault 管理です。/),
    ).toBeInTheDocument();
  });

  // BFF は後段障害を空一覧へ縮退させない（502）。「未登録」と誤認させて重複登録を招かないため。
  it('shows an error instead of an empty list when the query fails', async () => {
    mocks.apiFetch.mockRejectedValue(new ApiError('server', 'サーバでエラーが発生しました。', 500));
    await renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.queryByText('データソースは登録されていません。')).not.toBeInTheDocument();
  });

  it('reports an operation failure without losing the list', async () => {
    mocks.apiFetch.mockImplementation((path: string) =>
      path.endsWith('/sync')
        ? Promise.reject(new ApiError('server', 'サーバでエラーが発生しました。', 500))
        : Promise.resolve([ACTIVE_SOURCE]),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.getByText('規程集')).toBeInTheDocument();
  });

  it('shows a neutral message when there is no source', async () => {
    mocks.apiFetch.mockResolvedValue([]);
    await renderPage();

    expect(await screen.findByText('データソースは登録されていません。')).toBeInTheDocument();
  });

  // 存在秘匿（IADR-0009 / IADR-0035）: ロールを持たない利用者へ画面の存在を示さない。
  it('hides the screen from a user without any role', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE]);
    await renderPage([]);

    expect(screen.queryByRole('heading', { name: 'データソース' })).not.toBeInTheDocument();
    expect(mocks.apiFetch).not.toHaveBeenCalled();
  });

  // 導線: 計画の遷移図 SC06 → SC07。
  it('links to the conversion jobs screen (SC-07)', async () => {
    mocks.apiFetch.mockResolvedValue([]);
    await renderPage();

    expect(screen.getByRole('link', { name: '変換ジョブの状況を見る →' })).toHaveAttribute(
      'href',
      '/admin/conversions',
    );
  });

  // **実装しない要素**（画面仕様書 §hi-fi モックアップとの対応 #6・#7・#9）。
  // まず「見えるはずの条件」——一覧が描画され、手動同期の操作が出ている状態——を確かめてから、
  // 契約に無い列・操作が無いことを見る。
  it('does not render the next-sync column, the retry state, or a settings action', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE, DISABLED_SOURCE]);
    await renderPage();

    expect(await screen.findAllByRole('button', { name: '手動同期' })).toHaveLength(2);
    expect(screen.queryByRole('columnheader', { name: '次回同期' })).not.toBeInTheDocument();
    expect(screen.queryByText(/再試行中/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '設定' })).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    mocks.apiFetch.mockResolvedValue([ACTIVE_SOURCE]);
    activate('en');
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'Data sources' })).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('File server')).toBeInTheDocument();
  });
});
