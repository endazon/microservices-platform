import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-06, UC-04, FR-01/FR-02: データソース管理画面の再実装（#503）。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
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
  mocks.apiRequest.mockReset();
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
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE, DISABLED_SOURCE]));
    await renderPage();

    expect(await screen.findByText('規程集')).toBeInTheDocument();
    const table = within(screen.getByRole('table'));
    expect(table.getByText('ファイルサーバー')).toBeInTheDocument();
    expect(table.getByText('SaaS')).toBeInTheDocument();
    // active ＋ 最終同期あり → 同期済み。disabled → 無効（中立。琥珀は同期異常のために空けてある）。
    expect(table.getByText('同期済み')).toBeInTheDocument();
    expect(table.getByText('無効')).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/datasources',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // UC-04 例外フロー / SC-06（裁定 Q14 / #537）: 継続失敗を琥珀で見せ、直近エラーを添える。
  // 計画は「データソースの同期は**静かに壊れる**種類の機能であり、気づく手段が本画面の状態表示である」
  // と述べている。INDEX 決定 21 により、色だけでなくテキストでも異常が読める。
  it('shows an amber sync-fault state with the redacted last error', async () => {
    const FAILING_SOURCE = {
      ...ACTIVE_SOURCE,
      id: '33333333-3333-3333-3333-333333333333',
      name: '社内Wiki',
      sourceType: 'wiki',
      consecutiveFailureCount: 5,
      retryLimit: 5,
      // 後段が保存時点でマスク済みの文字列（IADR-0053 と同じ守り）。
      lastSyncError: 'connect failed: Host=db;Password=***',
      lastSyncErrorAt: '2026-08-08T02:00:00Z',
    };
    mocks.apiRequest.mockResolvedValue(jsonResponse([FAILING_SOURCE]));
    await renderPage();

    expect(await screen.findByText('社内Wiki')).toBeInTheDocument();
    const table = within(screen.getByRole('table'));
    expect(table.getByText('同期異常（5/5）')).toBeInTheDocument();
    expect(table.getByText('connect failed: Host=db;Password=***')).toBeInTheDocument();
    // 異常時に「同期済み」は出さない（取り込みが続いていると読めてしまう）。
    expect(table.queryByText('同期済み')).not.toBeInTheDocument();
  });

  // 上限未満は「再試行中」（hi-fi の「⚠ 再試行中（3/5）」）。まだ回復し得るが既に壊れかけている。
  it('shows an amber retrying state below the retry limit', async () => {
    mocks.apiRequest.mockResolvedValue(
      jsonResponse([{ ...ACTIVE_SOURCE, consecutiveFailureCount: 3, retryLimit: 5 }]),
    );
    await renderPage();

    expect(await screen.findByText('規程集')).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('再試行中（3/5）')).toBeInTheDocument();
  });

  // UC-04 基本フロー 1: 管理者がソース（ファイルサーバー／Wiki／SaaS／業務DB）を登録する。
  it('registers a data source with a default confidentiality attribute', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
    await user.click(screen.getByRole('button', { name: '登録する' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/datasources',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    const posted = mocks.apiRequest.mock.calls.find(
      ([, init]) => (init as RequestInit).method === 'POST',
    )!;
    expect(JSON.parse(String((posted[1] as RequestInit).body))).toEqual({
      name: '規程集',
      sourceType: 'filesystem',
      connectionUri: 'smb://fs01/share',
      defaultAttributes: { confidentiality: 'internal' },
    });
    expect(await screen.findByText('データソースを登録しました。')).toBeInTheDocument();
  });

  // 必須（名前・接続先）が埋まるまで登録できない。
  it('keeps the register button disabled until the required fields are filled', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
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
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/datasources/${ACTIVE_SOURCE.id}/sync`,
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('同期をトリガしました。')).toBeInTheDocument();
  });

  // IADR-0127 決定 5: 操作の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
  // これが外れると、同期をトリガしたのに最終同期日時が古いまま残る。
  it('refetches the list after a successful sync', async () => {
    mocks.apiRequest.mockImplementation((path: string) =>
      path.endsWith('/sync')
        ? Promise.resolve(noContent())
        : Promise.resolve(jsonResponse([ACTIVE_SOURCE])),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(mocks.apiRequest.mock.calls.filter(([path]) => path === '/datasources')).toHaveLength(
        2,
      ),
    );
  });

  it('disables an active source and offers no disable action for a disabled one', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE, DISABLED_SOURCE]));
    const user = userEvent.setup();
    await renderPage();

    // 無効化は active の行だけに出る（既に無効なソースへ再度送らない）。
    const buttons = await screen.findAllByRole('button', { name: '無効化' });
    expect(buttons).toHaveLength(1);

    await user.click(buttons[0]);
    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        `/datasources/${ACTIVE_SOURCE.id}`,
        expect.objectContaining({ method: 'DELETE' }),
      ),
    );
  });

  // UC-04 例外フロー（接続の継続失敗はアラート）に対応する静的な注記。
  it('states that credentials live in Vault and that repeated failures raise an alert', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(await screen.findByText(/接続情報（認証情報）は Vault 管理です。/)).toBeInTheDocument();
  });

  // BFF は後段障害を空一覧へ縮退させない（502）。「未登録」と誤認させて重複登録を招かないため。
  it('shows an error instead of an empty list when the query fails', async () => {
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.queryByText('データソースは登録されていません。')).not.toBeInTheDocument();
  });

  it('reports an operation failure without losing the list', async () => {
    mocks.apiRequest.mockImplementation((path: string) =>
      path.endsWith('/sync')
        ? Promise.reject(new ApiError('server', 'サーバでエラーが発生しました。', 500))
        : Promise.resolve(jsonResponse([ACTIVE_SOURCE])),
    );
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('button', { name: '手動同期' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.getByText('規程集')).toBeInTheDocument();
  });

  // IADR-0127 決定 7: 画面は直近の操作の結果だけを出す。TanStack Query は「別の」ミューテーションの
  // 成功では他方の isError を戻さないため、これが外れると成功バナーと古い失敗バナーが同時に出る。
  // 逆向き（古い成功バナーが新しい失敗の隣に残る）も同じ穴なので、両方向を 1 本で見る。
  it('shows only the latest operation result (neither a stale failure nor a stale success survives)', async () => {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (path.endsWith('/sync')) {
        return Promise.reject(new ApiError('server', 'サーバでエラーが発生しました。', 500));
      }
      return init?.method === 'DELETE'
        ? Promise.resolve(noContent())
        : Promise.resolve(jsonResponse([ACTIVE_SOURCE]));
    });
    const user = userEvent.setup();
    await renderPage();

    // 手動同期が失敗する。
    await user.click(await screen.findByRole('button', { name: '手動同期' }));
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );

    // 続けて無効化が成功する → 古い失敗バナーは残らない。
    await user.click(screen.getByRole('button', { name: '無効化' }));
    expect(await screen.findByText('データソースを無効化しました。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    // さらに手動同期が失敗する → 古い成功バナーも残らない。
    await user.click(screen.getByRole('button', { name: '手動同期' }));
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.queryByText('データソースを無効化しました。')).not.toBeInTheDocument();
  });

  it('shows a neutral message when there is no source', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    await renderPage();

    expect(await screen.findByText('データソースは登録されていません。')).toBeInTheDocument();
  });

  // 存在秘匿（IADR-0009 / IADR-0035）: ロールを持たない利用者へ画面の存在を示さない。
  it('hides the screen from a user without any role', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
    await renderPage([]);

    expect(screen.queryByRole('heading', { name: 'データソース' })).not.toBeInTheDocument();
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  // 導線: 計画の遷移図 SC06 → SC07。
  it('links to the conversion jobs screen (SC-07)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
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
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE, DISABLED_SOURCE]));
    await renderPage();

    expect(await screen.findAllByRole('button', { name: '手動同期' })).toHaveLength(2);
    expect(screen.queryByRole('columnheader', { name: '次回同期' })).not.toBeInTheDocument();
    expect(screen.queryByText(/再試行中/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '設定' })).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
    activate('en');
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'Data sources' })).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('File server')).toBeInTheDocument();
  });

  // SC-06, IADR-0135 決定 7［2026-08-06 追記］: 一覧が**本文なし**（204）で返っても画面は落ちない。
  //
  // 載せ替え前は `apiFetch` が空ボディで `undefined` を返すため `items = data ?? []` が実効ガード
  // だった。生成物の `bffFetch` は空ボディで `{}` を返すので `??` は発火せず、`items.length === 0`
  // も `{}.length === undefined` で救えず、`items.map` が `TypeError` を投げていた。
  // `okArray` が「配列でなければ空配列」まで詰めることで、載せ替え前と同じ縮退に戻る。
  it('degrades to the empty state when the list response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path.startsWith('/datasources')) return noContent();
      return jsonResponse([]);
    });
    await renderPage();

    expect(await screen.findByText('データソースは登録されていません。')).toBeInTheDocument();
  });
});
