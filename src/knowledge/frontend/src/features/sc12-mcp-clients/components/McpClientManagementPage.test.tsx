import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { QueryClient } from '@tanstack/react-query';
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
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'http://127.0.0.1:53123/callback',
    );
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

  // 05_screens §SC-12 の入力表（2026-10-09 追加）・ADR-0134 決定 1: 有人はリダイレクト URI が必須で、規則に外れた URI
  // （ワイルドカード・localhost）は送る前に止める。送る本文には有人のときだけ redirectUris を載せる（1 行 1 件）。
  it('requires redirect URIs only for the attended kind and sends them line by line', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });
    const posts = () =>
      mocks.apiRequest.mock.calls.filter(
        ([path, init]) =>
          String(path).endsWith('/mcp-clients') && (init as RequestInit)?.method === 'POST',
      );

    await user.type(screen.getByLabelText('クライアント ID'), 'cli-agent');
    await user.type(screen.getByLabelText('表示名'), 'CLI');
    // 否定形: 未入力・ワイルドカード・localhost は送らない。
    await user.click(screen.getByRole('button', { name: '登録' }));
    expect(await screen.findByTestId('registration-issues')).toHaveTextContent(
      '有人にはリダイレクト URI が 1 件以上必要です。',
    );
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'https://agent.example.test/*{enter}http://localhost:8080/cb',
    );
    await user.click(screen.getByRole('button', { name: '登録' }));
    expect(await screen.findByTestId('registration-issues')).toHaveTextContent(
      'リダイレクト URI は https か',
    );
    expect(posts()).toHaveLength(0);

    // 陽性対照: 正しい 2 件（https・ループバック）は 1 行 1 件のまま送る。
    await user.clear(screen.getByLabelText('リダイレクト URI（1 行に 1 件）'));
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'https://agent.example.test/cb{enter} http://127.0.0.1:53123/callback ',
    );
    await user.click(screen.getByRole('button', { name: '登録' }));
    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(JSON.parse(String((posts()[0][1] as RequestInit).body))).toMatchObject({
      kind: 'interactive',
      redirectUris: ['https://agent.example.test/cb', 'http://127.0.0.1:53123/callback'],
    });

    // 無人では入力欄が消え、本文にも載せない。
    await user.selectOptions(screen.getByLabelText('クライアント種別'), 'service-account');
    expect(screen.queryByTestId('redirect-uris')).not.toBeInTheDocument();
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
  // ★［UI/UX 改善 2026-09-12］三部品（NFR / ADR-0031）へ寄せたので、失敗は `QueryState` →
  // `ErrorState`（`role="alert"`）が描く。**testid ではなく役割と文言で引く**
  // （一覧と公開ツールの 2 本が同時に落ちるため `findAllByRole` を使う）。
  it('surfaces a fetch failure instead of degrading to an empty list', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'failed', 500, []));
    await renderPage();

    const alerts = await screen.findAllByRole('alert');
    expect(
      alerts.some((a) => a.textContent?.includes('登録クライアントを取得できませんでした。')),
    ).toBe(true);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    // 0 件の文言へ縮退していない（「1 件も登録が無い」と読ませない）。
    expect(screen.queryByText('登録されたクライアントはありません。')).not.toBeInTheDocument();
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
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'https://bot.example.test/cb',
    );
    await user.click(screen.getByRole('button', { name: '登録' }));

    expect(await screen.findByTestId('registration-error')).toHaveTextContent(
      'doc_scope=private-note は割り当てられません',
    );
  });

  // 🔴 SC-12, ADR-0062 決定 2・§結果 (#1185): **後段が名指しした「外れた値」を画面上のテキストとして出す。**
  //
  // 本画面は「割り当てられるか」を**事前に示さない**（部分集合の判定は後段だけが持つ。
  // 画面が判定すると API を直接叩けば素通しになる）。計画はその代償として
  // 「拒否応答にどの値が外れたかを含め、それを表示する」ことを緩和策に置いた。
  // **表示を落とすと、緩和策ごと消える。** `title` 属性ではなく本文として出ていることを測る。
  it('names the value that fell outside the registrar set when registering', async () => {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'POST' && String(path).endsWith('/mcp-clients')) {
        return Promise.reject(
          new ApiError('validation', 'bad request', 400, [
            "clearance の値 'confidential' は割り当てられません（登録者が持つ機密区分は 'internal', 'public' です）。",
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
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'https://bot.example.test/cb',
    );
    await user.click(screen.getByRole('button', { name: '登録' }));

    const alert = await screen.findByTestId('registration-error');
    expect(alert).toHaveTextContent("'confidential'");
    // ★ 陽性対照: 理由の本体も出ている（値だけを拾う実装で緑にしない）。
    expect(alert).toHaveTextContent('割り当てられません');
  });

  // 🔴 SC-12, ADR-0062 決定 3 (#1185): **差し替え経路の拒否理由も同じように出る。**
  // 登録だけ理由を出して差し替えが黙る形にしない（後段は同じ 1 つの関数で判定している）。
  it('names the value that fell outside the registrar set when replacing attributes', async () => {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'PUT' && String(path).includes('/attributes')) {
        return Promise.reject(
          new ApiError('validation', 'bad request', 400, [
            "tags の値 'finance' は割り当てられません（登録者が持つタグは 'hr', 'sales' です）。",
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

    const row = screen.getByText('夜間ダイジェスト').closest('tr') as HTMLElement;
    await user.click(within(row).getByRole('button', { name: '属性を変更' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    const alert = await screen.findByTestId('attribute-edit-error');
    expect(alert).toHaveTextContent("'finance'");
    // 🔴 **外れていない値を混ぜない**ことは後段の責務だが、画面が勝手に足さないことも測る。
    expect(alert).not.toHaveTextContent("'sales' は割り当てられません");
  });

  // 🔴 FR-16, UC-09, SC-12「無人アカウントの ABAC 属性割当」: **登録後の差し替え。**
  //
  // このテストが在る理由は、**後段に端点があり生成フックもあるのに画面から呼ばれていなかった**
  // からである（AI レビューが検出）。属性は登録時にしか置けず、機密区分を打ち間違えたら
  // クライアントを作り直すしかなかった。**「実装がある」と「使える」が別だという本画面の主題を、
  // 本画面自身が 1 経路で破っていた。**
  //
  // `check-knip` は分割代入のプロパティ単位まで見ないので、この型の死経路は機械検査を素通りする。
  // **経路が生きていることは、要求が実際に飛ぶことでしか測れない。**
  it('replaces the ABAC attributes of an already-registered unattended client', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    // 有人（dev-agent）には出さない。割り当てる対象が無いからである。
    expect(screen.getAllByRole('button', { name: '属性を変更' })).toHaveLength(2);

    await user.click(screen.getAllByRole('button', { name: '属性を変更' })[0]);
    expect(screen.getByTestId('attribute-edit-target')).toHaveTextContent('nightly-digest-bot');
    // 現在値が読み込まれていること。空から始めると「触らなかった属性が消える」。
    expect(screen.getByTestId('attribute-edit-entries')).toHaveTextContent(
      'confidentiality: internal',
    );

    // 登録フォーム側にも同じ文言のラベルがあるので、差し替え区画に閉じて引く。
    const editor = within(screen.getByTestId('attribute-edit'));
    await user.selectOptions(editor.getByLabelText('属性'), 'confidentiality');
    await user.selectOptions(editor.getByLabelText('値'), 'public');
    await user.click(editor.getByRole('button', { name: '属性を追加' }));
    await user.click(editor.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(([path, init]) => {
          const request = init as RequestInit;
          return (
            String(path).endsWith('/mcp-clients/nightly-digest-bot/attributes') &&
            request?.method === 'PUT' &&
            String(request?.body).includes('public')
          );
        }),
      ).toBe(true),
    );
  });

  // 陽性対照の対。**空の属性で保存させない** —— 無人アカウントの判定軸が消えるためであり、
  // 登録時に禁じているのと同じ理由である。
  it('refuses to save an empty attribute set for an unattended client', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await user.click(screen.getAllByRole('button', { name: '属性を変更' })[0]);
    const editor = within(screen.getByTestId('attribute-edit'));
    await user.click(editor.getByRole('button', { name: '削除' }));

    expect(screen.getByTestId('attribute-edit-empty')).toBeInTheDocument();
    expect(editor.getByRole('button', { name: '保存' })).toBeDisabled();

    await user.click(editor.getByRole('button', { name: '保存' }));
    // 🔴 `/attributes` だけで引かない —— 属性辞書の取得（`/authz/attributes`）に当たってしまい、
    // **常に true になって検出力を失う**（初版はこれで落ちた）。差し替えの経路だけを見る。
    expect(
      mocks.apiRequest.mock.calls.some(([path]) =>
        /\/mcp-clients\/[^/]+\/attributes$/.test(String(path)),
      ),
    ).toBe(false);
  });

  // IADR-0124 決定 5: ナビはデータであり `<Link to>` の静的検査が効かない。
  // ── #1845: 無人の client secret の一度だけの表示と再発行（05_screens §SC-12 の 2026-10-09 補完・ADR-0134 決定 2）──

  // 試験の値は明らかに偽物の綴りにする（本物の鍵の形をした文字列を置かない）。
  const ISSUED = 'placeholder-issued-value';
  const REISSUED = 'placeholder-reissued-value';

  /** 登録（201）と再発行（200）が secret を返す後段。それ以外は `mockApi` と同じ。 */
  function mockApiWithSecrets() {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'POST' && String(path).endsWith('/mcp-clients')) {
        const body = JSON.parse(String(init.body)) as { clientId: string; kind: string };
        return Promise.resolve(
          jsonResponse(
            {
              ...CLIENTS[1],
              clientId: body.clientId,
              kind: body.kind,
              clientSecret: body.kind === 'service-account' ? ISSUED : null,
            },
            201,
          ),
        );
      }
      if (init?.method === 'POST' && String(path).endsWith('/reissue-secret')) {
        return Promise.resolve(
          jsonResponse({ clientId: 'nightly-digest-bot', clientSecret: REISSUED }),
        );
      }
      if (String(path).includes('/mcp-clients/tools')) return Promise.resolve(jsonResponse(TOOLS));
      if (String(path).includes('/authz/attributes'))
        return Promise.resolve(jsonResponse(ATTRIBUTES));
      return Promise.resolve(jsonResponse(CLIENTS));
    });
  }

  async function registerServiceAccount(user: ReturnType<typeof userEvent.setup>) {
    await user.type(screen.getByLabelText('クライアント ID'), 'new-bot');
    await user.type(screen.getByLabelText('表示名'), '新ボット');
    await user.selectOptions(screen.getByLabelText('クライアント種別'), 'service-account');
    const form = within(screen.getByTestId('attribute-assignment'));
    await user.selectOptions(form.getByLabelText('属性'), 'confidentiality');
    await user.selectOptions(form.getByLabelText('値'), 'public');
    await user.click(form.getByRole('button', { name: '属性を追加' }));
    await user.click(screen.getByRole('button', { name: '登録' }));
  }

  it('shows the client secret of a new unattended client once, with a copy action and a no-redisplay notice', async () => {
    mockApiWithSecrets();
    // user-event はクリップボードを自前の器へ差し替える（setup の後に読める）。
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    // 否定形の前提: 登録の前は何も出ていない。
    expect(screen.queryByTestId('issued-secret')).not.toBeInTheDocument();
    await registerServiceAccount(user);

    const panel = await screen.findByTestId('issued-secret');
    expect(within(panel).getByTestId('issued-secret-value')).toHaveTextContent(ISSUED);
    expect(panel).toHaveTextContent('表示できるのは今回だけです');
    expect(panel).toHaveTextContent('再表示できません');
    expect(panel).toHaveTextContent('new-bot');

    await user.click(within(panel).getByRole('button', { name: 'コピー' }));
    expect(await navigator.clipboard.readText()).toBe(ISSUED);

    // 閉じたら消え、再表示の手段は無い（一覧にも値は出ない）。
    await user.click(within(panel).getByRole('button', { name: '閉じる' }));
    expect(screen.queryByTestId('issued-secret')).not.toBeInTheDocument();
    expect(screen.queryByText(ISSUED)).not.toBeInTheDocument();
  });

  it('shows no secret after registering an attended client (public client)', async () => {
    mockApiWithSecrets();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await user.type(screen.getByLabelText('クライアント ID'), 'human-agent');
    await user.type(screen.getByLabelText('表示名'), '有人エージェント');
    await user.type(
      screen.getByLabelText('リダイレクト URI（1 行に 1 件）'),
      'https://agent.example.test/cb',
    );
    await user.click(screen.getByRole('button', { name: '登録' }));
    // 陽性対照: 登録の要求は飛んでいる。
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/mcp-clients') && (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );
    expect(screen.queryByTestId('issued-secret')).not.toBeInTheDocument();
  });

  it('reissues the secret of an unattended client only after confirmation and shows the new value once', async () => {
    mockApiWithSecrets();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    // 有人（dev-agent）には出さない。無人の 2 行だけに出る。
    expect(screen.getAllByRole('button', { name: 'secret を再発行' })).toHaveLength(2);

    // 取消なら要求を送らない。
    await user.click(screen.getAllByRole('button', { name: 'secret を再発行' })[0]);
    const confirmation = await screen.findByTestId('reissue-confirmation');
    expect(confirmation).toHaveTextContent('nightly-digest-bot');
    expect(confirmation).toHaveTextContent('ただちに使えなくなります');
    await user.click(within(confirmation).getByRole('button', { name: '取消' }));
    expect(
      mocks.apiRequest.mock.calls.some(([path]) => String(path).endsWith('/reissue-secret')),
    ).toBe(false);

    await user.click(screen.getAllByRole('button', { name: 'secret を再発行' })[0]);
    await user.click(
      within(await screen.findByTestId('reissue-confirmation')).getByRole('button', {
        name: '再発行する',
      }),
    );

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/mcp-clients/nightly-digest-bot/reissue-secret') &&
            (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );
    const panel = await screen.findByTestId('issued-secret');
    expect(within(panel).getByTestId('issued-secret-value')).toHaveTextContent(REISSUED);
    expect(panel).toHaveTextContent('再表示できません');

    // 次の操作を始めたら捨てる（表示を残したまま別の操作へ進ませない）。
    await user.click(screen.getAllByRole('button', { name: '属性を変更' })[0]);
    expect(screen.queryByTestId('issued-secret')).not.toBeInTheDocument();
  });

  // ── #1845 の独立監査: 二重送信と、閉じた後の変更キャッシュ ──

  /** 指定した POST の応答を、テストが `release()` するまで止める（送信中の状態を作る）。 */
  function holdPost(suffix: string) {
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const passThrough = mocks.apiRequest.getMockImplementation()!;
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) =>
      init?.method === 'POST' && String(path).endsWith(suffix)
        ? gate.then(() => passThrough(path, init))
        : passThrough(path, init),
    );
    return () => release();
  }

  const postsTo = (suffix: string) =>
    mocks.apiRequest.mock.calls.filter(
      ([path, init]) => String(path).endsWith(suffix) && (init as RequestInit)?.method === 'POST',
    ).length;

  /** 変更キャッシュ（MutationCache）に残っている応答・変数の文字列。secret が残っていないかを見る。 */
  const mutationMemory = (client: QueryClient) =>
    JSON.stringify(
      client
        .getMutationCache()
        .getAll()
        .map((m) => [m.state.data, m.state.variables]),
    );

  it('sends only one reissue request while the first one is still in flight', async () => {
    mockApiWithSecrets();
    const release = holdPost('/reissue-secret');
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await user.click(screen.getAllByRole('button', { name: 'secret を再発行' })[0]);
    await user.click(
      within(await screen.findByTestId('reissue-confirmation')).getByRole('button', {
        name: '再発行する',
      }),
    );
    await waitFor(() => expect(postsTo('/reissue-secret')).toBe(1));

    // 送信中に同じ行から確認まで進もうとしても、2 本目は飛ばない（行のボタンが押せない）。
    await user.click(screen.getAllByRole('button', { name: 'secret を再発行' })[0]);
    const again = screen.queryByTestId('reissue-confirmation');
    if (again) {
      await user.dblClick(within(again).getByRole('button', { name: '再発行する' }));
    }

    release();
    await screen.findByTestId('issued-secret');
    expect(postsTo('/reissue-secret')).toBe(1);
  });

  it('sends only one registration while the first one is still in flight', async () => {
    mockApiWithSecrets();
    const release = holdPost('/mcp-clients');
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    await registerServiceAccount(user);
    await waitFor(() => expect(postsTo('/mcp-clients')).toBe(1));
    await user.dblClick(screen.getByRole('button', { name: '登録' }));

    release();
    await screen.findByTestId('issued-secret');
    expect(postsTo('/mcp-clients')).toBe(1);
  });

  it('leaves no secret in the mutation cache after the issued secret is closed', async () => {
    mockApiWithSecrets();
    const user = userEvent.setup();
    const { queryClient } = await renderPage();
    await screen.findByRole('table', { name: '登録された MCP クライアントの一覧' });

    // 登録で発行した secret: 表示の間は変更の結果に載っている（陽性対照）、閉じたら消える。
    await registerServiceAccount(user);
    const issuedPanel = await screen.findByTestId('issued-secret');
    expect(mutationMemory(queryClient)).toContain(ISSUED);
    await user.click(within(issuedPanel).getByRole('button', { name: '閉じる' }));
    await waitFor(() => expect(mutationMemory(queryClient)).not.toContain(ISSUED));

    // 再発行した secret も同じ（閉じる以外の「次の操作」で捨てる経路）。
    await user.click(screen.getAllByRole('button', { name: 'secret を再発行' })[0]);
    await user.click(
      within(await screen.findByTestId('reissue-confirmation')).getByRole('button', {
        name: '再発行する',
      }),
    );
    await screen.findByTestId('issued-secret');
    expect(mutationMemory(queryClient)).toContain(REISSUED);
    await user.click(screen.getAllByRole('button', { name: '属性を変更' })[0]);
    await waitFor(() => expect(mutationMemory(queryClient)).not.toContain(REISSUED));
  });

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
