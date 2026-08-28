import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-12, UC-09, FR-16, ADR-0024 (#452): MCP クライアント登録管理。
//
// IADR-0135 決定 4: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
//
// 🔴 **否定形（公開ツールの編集 UI が無い・権限外に画面が無い）は陽性対照と対で置く。**
// 何も描かない実装でも否定形だけなら緑になるためである。変異試験の結果は作業仕様書に残す。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc12McpClientsRoute, sc12McpClientsNav } from '../index';

const CLIENTS = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    clientId: 'dev-agent',
    displayName: '開発部エージェント',
    kind: 'interactive',
    enabled: true,
    attributes: {},
    egressTier: 'protected-external',
    registeredAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    clientId: 'nightly-digest-bot',
    displayName: '夜間ダイジェスト',
    kind: 'service-account',
    enabled: true,
    attributes: { confidentiality: 'internal' },
    egressTier: 'self-hosted',
    registeredAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
  {
    id: '33333333-3333-3333-3333-333333333333',
    clientId: 'legacy-agent',
    displayName: '旧検証エージェント',
    kind: 'service-account',
    enabled: false,
    attributes: {},
    egressTier: 'standard-external',
    registeredAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
];

const TOOLS = {
  version: 3,
  tools: [
    {
      name: 'retrieval.search_documents',
      service: 'retrieval-service',
      description: '横断検索',
      requiredScope: 'document:read',
      egressClass: 'metadata-only',
    },
  ],
  drifts: [{ kind: 'UndeclaredTool', target: 'graph.traverse', detail: '申告に無い' }],
};

const ATTRIBUTES = [
  {
    id: 'a1',
    key: 'confidentiality',
    label: '機密区分上限',
    allowedValues: ['public', 'internal'],
    required: true,
    scope: 'user',
  },
  { id: 'a2', key: 'tags', label: 'タグ', allowedValues: ['週報'], required: false, scope: 'user' },
  // 🔴 **文書スコープの属性は主体へ割り当てない**（意味が反転する）。選択肢に出ないことを測る。
  {
    id: 'a3',
    key: 'doc_scope',
    label: '文書区分',
    allowedValues: ['private-note'],
    required: false,
    scope: 'document',
  },
];

/** 経路ごとに応答を振り分ける（1 画面が 3 本引くため、URL で分けないと取り違える）。 */
function mockApi(overrides: { clients?: unknown; tools?: unknown } = {}) {
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path.includes('/mcp-clients/tools')) {
      return Promise.resolve(jsonResponse(overrides.tools ?? TOOLS));
    }
    if (path.includes('/authz/attributes')) return Promise.resolve(jsonResponse(ATTRIBUTES));
    if (path.includes('/mcp-clients')) {
      return Promise.resolve(jsonResponse(overrides.clients ?? CLIENTS));
    }
    return Promise.resolve(jsonResponse([]));
  });
}

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc12McpClientsRoute(shell)], {
    initialEntry: '/admin/mcp-clients',
    roles,
  });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('McpClientManagementPage (SC-12)', () => {
  // 05_screens §SC-12 主要素 1: 登録クライアント一覧（種別・認証・属性・状態）。
  it('lists the registered clients with kind, auth method and state', async () => {
    mockApi();
    await renderPage();

    expect(
      await screen.findByRole('heading', { name: 'MCP クライアント登録管理' }),
    ).toBeInTheDocument();

    const table = within(
      await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' }),
    );
    expect(table.getByText('開発部エージェント')).toBeInTheDocument();
    expect(table.getByText('夜間ダイジェスト')).toBeInTheDocument();
    // 種別は 2 値で、認証方式が併記される（モックの「認証」列）。
    expect(table.getByText('Authorization Code + PKCE')).toBeInTheDocument();
    expect(table.getAllByText('Client Credentials')).toHaveLength(2);
    // INDEX 決定 21: 状態は色 ＋ アイコン ＋ テキスト。無効は「いつから効くか」まで書く。
    expect(table.getAllByText('有効')).toHaveLength(2);
    expect(table.getByText('無効（即時接続拒否）')).toBeInTheDocument();
    // 有人は空欄にせず「利用者の属性で解決」と書く（割り当て忘れと読ませない）。
    expect(table.getByText('利用者の属性で解決')).toBeInTheDocument();
    expect(table.getByText('confidentiality: internal')).toBeInTheDocument();
  });

  // 無効化は次の呼び出しから即座に効く。画面は無効化／再有効化を出し分ける。
  it('sends a disable request for an enabled client and an enable request for a disabled one', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await user.click(screen.getAllByRole('button', { name: '無効化' })[0]);
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/mcp-clients/dev-agent/disable') &&
            (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );

    await user.click(screen.getByRole('button', { name: '再有効化' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(([path]) =>
          String(path).endsWith('/mcp-clients/legacy-agent/enable'),
        ),
      ).toBe(true),
    );
  });

  // 05_screens §SC-12 入力/バリデーション: 無人時は ABAC 属性が必須。**有人では要求しない。**
  it('requires ABAC attributes only for the unattended kind', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    // 陽性対照: 有人なら属性の入力欄が出ず、属性なしでも登録要求が飛ぶ。
    expect(screen.queryByTestId('attribute-assignment')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('クライアント ID'), 'new-agent');
    await user.type(screen.getByLabelText('表示名'), '新エージェント');
    await user.click(screen.getByRole('button', { name: '登録' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/mcp-clients') && (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );

    // 無人へ切り替えると属性の入力欄が出て、属性なしの登録は止まる。
    await user.type(screen.getByLabelText('クライアント ID'), 'bot');
    await user.type(screen.getByLabelText('表示名'), 'ボット');
    await user.selectOptions(screen.getByLabelText('クライアント種別'), 'service-account');
    expect(screen.getByTestId('attribute-assignment')).toBeInTheDocument();

    const before = mocks.apiRequest.mock.calls.length;
    await user.click(screen.getByRole('button', { name: '登録' }));
    expect(await screen.findByTestId('registration-issues')).toHaveTextContent(
      '無人（サービスアカウント）には ABAC 属性の割当が必須です。',
    );
    expect(mocks.apiRequest.mock.calls.length).toBe(before);
  });

  // 05_screens §SC-12: 定義済みの属性・許可値のみ。**文書スコープの属性は主体へ割り当てない。**
  it('offers only the subject-scoped dictionary entries and their allowed values', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });
    await user.selectOptions(screen.getByLabelText('クライアント種別'), 'service-account');

    const keySelect = screen.getByLabelText('属性');
    // 陽性対照（利用者スコープ 2 件は出る）と否定形（文書スコープは出ない）を対で置く。
    expect(within(keySelect).getByRole('option', { name: '機密区分上限' })).toBeInTheDocument();
    expect(within(keySelect).getByRole('option', { name: 'タグ' })).toBeInTheDocument();
    expect(within(keySelect).queryByRole('option', { name: '文書区分' })).not.toBeInTheDocument();

    await user.selectOptions(keySelect, 'confidentiality');
    const valueSelect = screen.getByLabelText('値');
    expect(within(valueSelect).getByRole('option', { name: 'internal' })).toBeInTheDocument();
    // 別の属性の許可値は混ざらない。
    expect(within(valueSelect).queryByRole('option', { name: '週報' })).not.toBeInTheDocument();

    await user.selectOptions(valueSelect, 'internal');
    await user.click(screen.getByRole('button', { name: '属性を追加' }));
    expect(screen.getByTestId('attribute-entries')).toHaveTextContent('confidentiality: internal');
  });

  // 05_screens §SC-12 主要素 4 / ADR-0024 §5: 実効ツール一覧と構成ドリフト。
  it('shows the effective tools and the configuration drift', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByTestId('published-tools')).toHaveTextContent(
      'retrieval.search_documents',
    );
    // ドリフトは握り潰さない（「公開しているつもりの公開されていない」の唯一の出口）。
    expect(screen.getByTestId('tool-drifts')).toHaveTextContent('graph.traverse');
  });

  // 🔴 05_screens §SC-12 アクション: 公開ツールの変更は本画面から行わない（GitOps へ誘導）。
  // **先に「操作可能な要素が在る」ことを確かめてから測る**（何も描かない実装でも緑にしない）。
  it('offers no way to edit the published tools and says where the change is made', async () => {
    mockApi();
    await renderPage();
    await screen.findByTestId('published-tools');

    // 陽性対照: 画面には操作可能な要素が在る（無効化・登録）。
    expect(screen.getAllByRole('button', { name: '無効化' }).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: '登録' })).toBeInTheDocument();

    // 否定形: 公開ツールを変更する操作は 1 つも無い。
    for (const name of ['ツールを追加', 'ツールを公開', 'ツールを削除', '公開ツールを編集']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.getByTestId('tools-readonly-notice')).toHaveTextContent(
      '変更は Git 上の公開構成を更新して反映します',
    );
  });

  // IADR-0009 / IADR-0035: 権限外には画面の存在を示さない（RequireRole → NotFound）。
  it('hides the screen from non-admins (existence hiding)', async () => {
    mockApi();
    await renderPage(['platform-operator']);

    await waitFor(() =>
      expect(
        screen.queryByRole('heading', { name: 'MCP クライアント登録管理' }),
      ).not.toBeInTheDocument(),
    );
    // 陽性対照は上のテスト群（管理者では見出しが出る）。ここでは一覧も引かないことまで測る。
    expect(
      mocks.apiRequest.mock.calls.some(([path]) => String(path).includes('/mcp-clients')),
    ).toBe(false);
  });

  // 🔴 取得失敗を空の一覧へ潰さない（「1 件も無い」と「引けない」は別の意味である）。
  it('surfaces a fetch failure instead of degrading to an empty list', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'failed', 500, []));
    await renderPage();

    expect(await screen.findByTestId('clients-error')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  // 後段の拒否理由（RFC7807）をそのまま出す。中立化すると管理者が直せなくなる。
  it('shows the downstream rejection reason for a forbidden attribute assignment', async () => {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'POST' && String(path).endsWith('/mcp-clients')) {
        return Promise.reject(
          new ApiError('validation', 'bad request', 400, [
            "サービスアカウント 'bot' へ doc_scope=private-note は割り当てられません",
          ]),
        );
      }
      if (String(path).includes('/mcp-clients/tools')) return Promise.resolve(jsonResponse(TOOLS));
      if (String(path).includes('/authz/attributes'))
        return Promise.resolve(jsonResponse(ATTRIBUTES));
      return Promise.resolve(jsonResponse(CLIENTS));
    });
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await user.type(screen.getByLabelText('クライアント ID'), 'bot');
    await user.type(screen.getByLabelText('表示名'), 'ボット');
    await user.click(screen.getByRole('button', { name: '登録' }));

    expect(await screen.findByTestId('registration-error')).toHaveTextContent(
      'doc_scope=private-note は割り当てられません',
    );
  });

  // IADR-0124 決定 5: ナビはデータであり `<Link to>` の静的検査が効かない。
  it('publishes a nav item in the admin group that resolves to the route', async () => {
    expect(sc12McpClientsNav.group).toBe('admin');
    expect(sc12McpClientsNav.requiresAnyRole).toEqual(['platform-admin']);

    mockApi();
    await renderUnitRoute((shell) => [createSc12McpClientsRoute(shell)], {
      initialEntry: sc12McpClientsNav.to,
      roles: ['platform-admin'],
    });

    expect(
      await screen.findByRole('heading', { name: 'MCP クライアント登録管理' }),
    ).toBeInTheDocument();
  });
});
