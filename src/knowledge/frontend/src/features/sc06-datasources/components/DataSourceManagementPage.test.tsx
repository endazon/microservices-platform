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

import { createSc06DataSourcesRoute } from '../routes/sc06DataSourcesRoute';

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
    // FR-05, UC-04, SC-06（#767）: 未入力の `department` は**キーごと送らない**。上の `toEqual` でも
    // 落ちるが、意図を名指しで固定する（空文字を送る形へ戻したときに理由が読める）。
    const attrs = JSON.parse(String((posted[1] as RequestInit).body)).defaultAttributes;
    expect(Object.keys(attrs)).not.toContain('department');
    // FR-05, UC-04, SC-06（#796）: 未指定の `lifecycle` も**キーごと送らない**。終端の `active` は
    // 「指定が無いときだけ」効くので、空文字や `active` を送る形へ戻すと**指定の有無が区別できなくなる**。
    expect(Object.keys(attrs)).not.toContain('lifecycle');
    expect(await screen.findByText('データソースを登録しました。')).toBeInTheDocument();
  });

  // FR-05, UC-04, SC-06（#767）: 計画 09_datasource-connectors §システム投入経路の **2 段目**
  // （データソースの既定属性から `department` を補う）を管理者が埋められること。
  // これが無いと画面から登録した全ソースが 3 段目の予約値 `unassigned` へ倒れ、ABAC の判定軸が
  // 実質 `confidentiality` 1 本になる。前後の空白は落とす（部門コードに空白は意味を持たない）。
  it('registers a data source with a default department attribute', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
    await user.type(screen.getByLabelText(/既定の部門/), '  開発  ');
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
      defaultAttributes: { confidentiality: 'internal', department: '開発' },
    });
  });

  // FR-05, UC-04, SC-06（#796）: 計画 09_datasource-connectors §システム投入経路の **2 段目**
  // （データソースの既定属性で `lifecycle` を指定する）を管理者が埋められること。計画は
  // 「**ソース単位で下書き扱いにしたい場合は、データソースの既定属性で `draft` を指定する**」と明記しており、
  // この欄が無いと画面からその指定ができない（API を直接叩くほかない）。
  it('registers a data source with a default lifecycle attribute', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
    await user.selectOptions(screen.getByLabelText(/既定のライフサイクル状態/), 'draft');
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
      defaultAttributes: { confidentiality: 'internal', lifecycle: 'draft' },
    });
  });

  // 値域は計画（07_abac-attribute-model の `lifecycle` 属性）が正である。**実装が語彙を増やさない** ——
  // 計画は `normalized` / `published` を名指しで「計画側の語彙ではない」と書いている（05_screens §SC-05）。
  it('offers exactly the three lifecycle states the plan defines, plus an unspecified choice', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    const select = screen.getByLabelText(/既定のライフサイクル状態/);
    expect(
      within(select)
        .getAllByRole('option')
        .map((option) => (option as HTMLOptionElement).value),
    ).toEqual(['', 'draft', 'active', 'archived']);
    // 未指定が既定の選択である（`active` を初期選択にすると「指定した」と「しなかった」が混ざる）。
    expect((select as HTMLSelectElement).value).toBe('');
  });

  // ライフサイクル状態は**任意**である（必須にすると計画に無い入力必須を実装が足すことになる）。
  it('does not require a lifecycle to enable the register button', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');

    expect(screen.getByRole('button', { name: '登録する' })).toBeEnabled();
    // 未指定時に何が起きるかを画面が伝える。**「予約値」ではなく「既定値」である**（IADR-0199 決定 4）。
    expect(screen.getByText(/未指定のときは既定値 active が入ります。/)).toBeInTheDocument();
  });

  // 部門は**任意**である（必須にすると計画に無い入力必須を実装が足すことになる）。
  it('does not require a department to enable the register button', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([]));
    const user = userEvent.setup();
    await renderPage();

    await user.click(screen.getByRole('button', { name: '＋ ソース登録' }));
    await user.type(screen.getByLabelText(/名前/), '規程集');
    await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');

    expect(screen.getByRole('button', { name: '登録する' })).toBeEnabled();
    // 未入力時に何が起きるかを画面が伝える（予約値そのものは翻訳しない）。
    expect(screen.getByText(/未入力のときは予約値 unassigned が入ります。/)).toBeInTheDocument();
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

  // FR-01, UC-04, SC-06（#628）: 計画 §SC-06「**登録・更新・無効化は管理者限定**」（裁定 Q19）。
  // #502 が確立した規則——**押しても結果が変わらないボタンを置かない**——に従い、運用者へは
  // 登録・無効化を出さない。**理由を書いて消す**（無言で消すと「登録できない画面」に見え、
  // 権限の問題と状態の問題を区別できない。[[IADR-0127]] 決定 1）。
  it('hides the create and disable actions from an operator, with a reason', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
    await renderPage(['platform-operator']);

    expect(await screen.findByText('規程集')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '＋ ソース登録' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '無効化' })).not.toBeInTheDocument();
    expect(screen.getByText('ソースの登録・無効化は管理者のみ実行できます')).toBeInTheDocument();
  });

  // planning#299（2026-08-09 裁定）: **手動同期は破壊的操作に含めない。**運用者にも出したままにする——
  // 運用者が SC-10 で異常に気づいたその場で再同期して一次対応できることを優先する。
  // **閲覧を狭めないこと**（一覧が読めること）も対で固定する。
  it('keeps the list and the manual sync action available to an operator', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE, DISABLED_SOURCE]));
    await renderPage(['platform-operator']);

    expect(await screen.findAllByRole('button', { name: '手動同期' })).toHaveLength(2);
    expect(screen.getByRole('table')).toBeInTheDocument();
  });

  // 狭めすぎていないこと: 管理者には 3 つとも出る。
  it('shows create, sync, and disable to an administrator', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
    await renderPage(['platform-admin']);

    expect(await screen.findByRole('button', { name: '＋ ソース登録' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '手動同期' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '無効化' })).toBeInTheDocument();
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
  // 契約に無い列・操作が無いことを見る。**接続先・認証情報の「設定」編集は依然として未実装**である
  // （#534 の射程のまま。#754 で足したのは既定属性の編集だけであり、別のボタンである）。
  it('does not render the next-sync column, the retry state, or a connection settings action', async () => {
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
    const user = userEvent.setup();
    await renderPage();

    expect(await screen.findByRole('heading', { name: 'Data sources' })).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('File server')).toBeInTheDocument();

    // #767: 登録フォームの項目にも訳が付いていること（ja だけ足して en を空のまま残さない）。
    // 未翻訳キーそのものは `scripts/check-i18n-catalogs.js` が全ロケール横断で止める。
    await user.click(screen.getByRole('button', { name: '+ Register source' }));
    expect(screen.getByLabelText('Default department')).toBeInTheDocument();
    // #796: 3 つ目の既定属性も同じ扱い。**値そのものは訳さない**（保存値であるため）ので、
    // 訳が要るのはラベルと「未指定」の選択肢である。
    const lifecycle = screen.getByLabelText('Default lifecycle state');
    expect(within(lifecycle).getByRole('option', { name: 'Unspecified' })).toBeInTheDocument();
    expect(within(lifecycle).getByRole('option', { name: 'draft' })).toBeInTheDocument();
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

  // FR-05, UC-04, SC-06（#754）: 既定属性の**更新**経路。
  //
  // 計画 §SC-06「既定属性の入力欄」（確定・2026-08-16）は「**登録・更新フォーム**は既定属性 3 つを
  // 持つ」と定める。登録側（#767 / #796）だけでは、**登録済みソースの部門を後から設定できない** ——
  // 供給源②（データソースの既定属性）が登録時の 1 回しか開かず、既存ソースは `unassigned` のまま残る。
  describe('default attribute editing (#754)', () => {
    // 予約値は「解決できなかったことの記録」であり部門名ではない（abac/department.ts）。
    // 保存済みの `unassigned` を入力欄へ出すと、管理者が実在の部門名と読んで**明示指定として
    // 送り返す**。すると「解決できなかった」と「管理者がそう指定した」の区別が消える。
    const RESERVED_SOURCE = {
      ...ACTIVE_SOURCE,
      defaultAttributes: {
        confidentiality: 'internal',
        department: 'unassigned',
        lifecycle: 'active',
        owner: 'system',
      },
    };

    function patchedBody() {
      const call = mocks.apiRequest.mock.calls.find(
        ([, init]) => (init as RequestInit).method === 'PATCH',
      )!;
      return JSON.parse(String((call[1] as RequestInit).body));
    }

    it('prefills the stored attributes and patches the full intent', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([RESERVED_SOURCE]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));

      // 予約値は**空欄**として見せる（実在の部門名と読ませない）。
      expect(screen.getByLabelText(/既定の部門/)).toHaveValue('');
      // `active` は予約値ではなく「そう決めた既定値」なので、保存値をそのまま出す。
      expect(screen.getByLabelText(/既定のライフサイクル状態/)).toHaveValue('active');
      expect(screen.getByLabelText(/既定の機密区分/)).toHaveValue('internal');

      await user.type(screen.getByLabelText(/既定の部門/), '開発');
      await user.click(screen.getByRole('button', { name: '更新する' }));

      await waitFor(() =>
        expect(mocks.apiRequest).toHaveBeenCalledWith(
          `/datasources/${ACTIVE_SOURCE.id}`,
          expect.objectContaining({ method: 'PATCH' }),
        ),
      );
      expect(patchedBody().defaultAttributes).toMatchObject({
        confidentiality: 'internal',
        department: '開発',
        lifecycle: 'active',
      });
      expect(await screen.findByText('既定属性を更新しました。')).toBeInTheDocument();
    });

    // 予約値を**明示指定として送り返さない**。空欄のまま更新したら、キーごと落ちる。
    it('never sends the reserved department value back', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([RESERVED_SOURCE]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));
      await user.click(screen.getByRole('button', { name: '更新する' }));

      await waitFor(() => expect(patchedBody()).toBeDefined());
      expect(Object.keys(patchedBody().defaultAttributes)).not.toContain('department');
    });

    // 🔴 PATCH の `defaultAttributes` は**全置換**である（バックエンド `DataSource.Patch`）。
    // 自分が管理しない属性（API から明示指定された `owner` 等）を土台に残さないと、
    // 画面から部門を直すたびに**所有者が消えて予約値へ落ちる**。ADR-0036 の裁量制御が壊れる。
    it('preserves attributes it does not manage (full-replacement semantics)', async () => {
      mocks.apiRequest.mockResolvedValue(
        jsonResponse([
          {
            ...ACTIVE_SOURCE,
            defaultAttributes: { confidentiality: 'internal', owner: 'alice@example.com' },
          },
        ]),
      );
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));
      await user.type(screen.getByLabelText(/既定の部門/), '経理');
      await user.click(screen.getByRole('button', { name: '更新する' }));

      await waitFor(() => expect(patchedBody()).toBeDefined());
      expect(patchedBody().defaultAttributes.owner).toBe('alice@example.com');
    });

    // 🔴 PUT ではなく PATCH を使う理由そのもの。GET 応答の `config` は秘密がマスク済み（`***`）で
    // あり、それを書き戻すと**認証情報を破壊する**（IADR-0053 / IADR-0148 決定 6）。
    // `config` を送らないことを名指しで固定する。
    it('does not send config back (avoids writing masked secrets)', async () => {
      mocks.apiRequest.mockResolvedValue(
        jsonResponse([{ ...ACTIVE_SOURCE, config: { apiToken: '***' } }]),
      );
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));
      await user.click(screen.getByRole('button', { name: '更新する' }));

      await waitFor(() => expect(patchedBody()).toBeDefined());
      expect(Object.keys(patchedBody())).not.toContain('config');
    });

    // 計画 §SC-06「登録・更新・無効化は管理者限定」。押しても 403 になるボタンを置かない（#502）。
    it('hides the edit action from non-admins', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([ACTIVE_SOURCE]));
      await renderPage(['platform-operator']);

      expect(await screen.findByText('規程集')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: '既定属性' })).not.toBeInTheDocument();
    });
  });

  // FR-05, UC-04, SC-06, ADR-0074 決定 1・4 (#1194): `owner` の**写像表**。
  //
  // 計画 ADR-0074 決定 1 は「`owner` の②の写像表は SC-06 の登録・更新フォームが持つ。
  // データソース単位で『ソース側識別子 → 利用者識別子』の対を並べる欄とし、**既定属性 3 つと
  // 同じ面・同じ権限**に置く」と定める。**新しい画面 ID も新しい権限も作らない。**
  describe('owner mapping table (#1194)', () => {
    const MAPPED_SOURCE = {
      ...ACTIVE_SOURCE,
      ownerMappings: { 'hr_system:tanaka': 'tanaka', 'hr_system:suzuki': 'suzuki' },
    };

    function bodyOf(method: string) {
      const call = mocks.apiRequest.mock.calls.find(
        ([, init]) => (init as RequestInit).method === method,
      )!;
      return JSON.parse(String((call[1] as RequestInit).body));
    }

    it('sends a mapping entered on the register form', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '＋ ソース登録' }));
      await user.type(screen.getByLabelText(/名前/), '人事DB');
      await user.type(screen.getByLabelText(/接続先 URI/), 'postgres://db/records');

      await user.click(screen.getByRole('button', { name: '＋ 写像を追加' }));
      await user.type(screen.getByLabelText('ソース側の利用者 1'), 'hr_system:tanaka');
      await user.type(screen.getByLabelText('基盤の利用者 1'), 'tanaka');
      await user.click(screen.getByRole('button', { name: '登録する' }));

      await waitFor(() => expect(bodyOf('POST')).toBeDefined());
      expect(bodyOf('POST').ownerMappings).toEqual({ 'hr_system:tanaka': 'tanaka' });
      // 🔴 **既定属性へ混ぜない。** 混ぜると片方の更新がもう片方を消す（ADR-0074 決定 1 が
      // 器を分けた理由そのもの）。
      expect(Object.keys(bodyOf('POST').defaultAttributes)).not.toContain('owner');
    });

    // **空の写像表はキーごと送らない**（既定属性の `department` / `lifecycle` と同じ規約）。
    // 送ると「管理者が空にした」と「触っていない」の区別が消える。
    it('omits the key entirely when no mapping is entered', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '＋ ソース登録' }));
      await user.type(screen.getByLabelText(/名前/), '規程集');
      await user.type(screen.getByLabelText(/接続先 URI/), 'smb://fs01/share');
      // 「＋」を押しただけの**空行**は送らない（管理者の入力の誤りではない）。
      await user.click(screen.getByRole('button', { name: '＋ 写像を追加' }));
      await user.click(screen.getByRole('button', { name: '登録する' }));

      await waitFor(() => expect(bodyOf('POST')).toBeDefined());
      expect(Object.keys(bodyOf('POST'))).not.toContain('ownerMappings');
    });

    it('prefills the stored mappings on the edit form and patches them independently', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([MAPPED_SOURCE]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));

      // 保存済みの 2 対が開く（キー昇順で安定させる。辞書の列挙順に依存しない）。
      expect(screen.getByLabelText('ソース側の利用者 1')).toHaveValue('hr_system:suzuki');
      expect(screen.getByLabelText('ソース側の利用者 2')).toHaveValue('hr_system:tanaka');

      await user.click(screen.getByRole('button', { name: '写像を削除 1' }));
      await user.click(screen.getByRole('button', { name: '更新する' }));

      await waitFor(() => expect(bodyOf('PATCH')).toBeDefined());
      expect(bodyOf('PATCH').ownerMappings).toEqual({ 'hr_system:tanaka': 'tanaka' });
      // 🔴 既定属性は**同じ要求で独立に**運ばれる（片方の更新がもう片方を消さない）。
      expect(bodyOf('PATCH').defaultAttributes).toMatchObject({ confidentiality: 'internal' });
    });

    // 🔴 **サーバが拒否した理由を画面に出す**（#1194 受け入れ基準 2）。
    // 後段は RFC7807（`errors`）で返すので `ApiError.details` に載る。
    it('shows the server reason when a mapping target does not exist', async () => {
      mocks.apiRequest
        .mockResolvedValueOnce(jsonResponse([ACTIVE_SOURCE]))
        .mockRejectedValueOnce(
          new ApiError('validation', '入力内容に誤りがあります。', 400, [
            '写像先の利用者が存在しません: nobody。利用者識別子（ログイン名）で指定してください。',
          ]),
        );
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '既定属性' }));
      await user.click(screen.getByRole('button', { name: '＋ 写像を追加' }));
      await user.type(screen.getByLabelText('ソース側の利用者 1'), 'src');
      await user.type(screen.getByLabelText('基盤の利用者 1'), 'nobody');
      await user.click(screen.getByRole('button', { name: '更新する' }));

      expect(await screen.findByText(/写像先の利用者が存在しません: nobody/)).toBeInTheDocument();
    });

    // 予約値 `system` は**文書側の属性**に入る値であって写像表の値ではない。
    // 補助文がそれを伝える（`lib/abac/owner.ts` の規約）。
    it('tells the admin what happens when a mapping is missing', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([]));
      const user = userEvent.setup();
      await renderPage();

      await user.click(await screen.findByRole('button', { name: '＋ ソース登録' }));
      expect(screen.getByText(/予約値 system になります/)).toBeInTheDocument();
    });
  });

  // FR-05, UC-04, SC-06, ADR-0074 決定 1 (#1252): **閲覧は管理者・運用者**である。
  //
  // 写像表と既定属性 3 つは「同じ面・同じ権限」に置かれる。従前は描画点が「既定属性」ボタン
  // （管理者のみ）から開くフォームしか無く、**運用者はどちらも見られなかった** ——
  // 「同じ権限」が「運用者にはどちらも見えない」という形でしか成立していなかった。
  describe('read-only attributes and owner mappings for operators (#1252)', () => {
    const FULLY_ATTRIBUTED_SOURCE = {
      ...ACTIVE_SOURCE,
      defaultAttributes: {
        confidentiality: 'confidential',
        department: '経理',
        lifecycle: 'active',
      },
      ownerMappings: { 'hr_system:tanaka': 'tanaka', 'hr_system:suzuki': 'suzuki' },
    };

    it('lets an operator read the default attributes and the owner mappings', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([FULLY_ATTRIBUTED_SOURCE]));
      await renderPage(['platform-operator']);

      expect(await screen.findByText('規程集')).toBeInTheDocument();
      const table = within(screen.getByRole('table'));
      // 既定属性 3 つが**値として**読める（ラベルだけでなく値が出る）。
      expect(table.getByText('confidential')).toBeInTheDocument();
      expect(table.getByText('経理')).toBeInTheDocument();
      expect(table.getByText('active')).toBeInTheDocument();
      // owner 写像表の対が読める（キー昇順で安定）。
      const mappings = within(table.getByRole('list', { name: '所有者の写像' }));
      expect(mappings.getByText('hr_system:suzuki')).toBeInTheDocument();
      expect(mappings.getByText('hr_system:tanaka')).toBeInTheDocument();
      expect(mappings.getAllByText(/^(tanaka|suzuki)$/)).toHaveLength(2);
    });

    // 🔴 **閲覧を開いても更新の口は開かない**（登録・更新は管理者限定）。
    // 既存の `hides the edit action from non-admins` と対で、写像表の入力欄も無いことを固定する。
    it('gives an operator no way to change them', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([FULLY_ATTRIBUTED_SOURCE]));
      await renderPage(['platform-operator']);

      expect(await screen.findByText('規程集')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: '既定属性' })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: '＋ 写像を追加' })).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: '更新する' })).not.toBeInTheDocument();
      expect(screen.queryByLabelText('ソース側の利用者 1')).not.toBeInTheDocument();
    });

    // **陽性対照**: 管理者は同じ値を読め、かつ従来どおり編集の口を持つ。
    // 権限で**内容**を出し分けていないこと（＝面が 1 つであること）をここで固定する。
    it('shows an admin the same values and keeps the edit affordance', async () => {
      mocks.apiRequest.mockResolvedValue(jsonResponse([FULLY_ATTRIBUTED_SOURCE]));
      await renderPage(['platform-admin']);

      const table = within(await screen.findByRole('table'));
      expect(table.getByText('confidential')).toBeInTheDocument();
      expect(table.getByText('経理')).toBeInTheDocument();
      expect(
        within(table.getByRole('list', { name: '所有者の写像' })).getByText('hr_system:tanaka'),
      ).toBeInTheDocument();
      expect(screen.getByRole('button', { name: '既定属性' })).toBeInTheDocument();
    });

    // 値が無いときに**空欄で終わらせない**（空欄は「取得できていない」とも読める）。
    // 予約値の説明も出す —— `unassigned` / `system` は解決できなかったことの記録である。
    it('says what happens when nothing is configured', async () => {
      mocks.apiRequest.mockResolvedValue(
        jsonResponse([{ ...ACTIVE_SOURCE, defaultAttributes: {}, ownerMappings: {} }]),
      );
      await renderPage(['platform-operator']);

      expect(await screen.findByText('規程集')).toBeInTheDocument();
      expect(screen.getByText(/予約値 unassigned が入ります/)).toBeInTheDocument();
      expect(screen.getByText(/写像に無い利用者は予約値 system になります/)).toBeInTheDocument();
    });
  });
});
