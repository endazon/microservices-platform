import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-17, UC-05, FR-05, FR-09, ADR-0026 (#452): ユーザーアカウント管理。
//
// IADR-0135 決定 4: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
//
// 🔴 **否定形（新規作成の口が無い・権限外に画面が無い・タグが必須でない）は陽性対照と対で置く。**
// 何も描かない実装でも否定形だけなら緑になるためである。変異試験の結果は作業仕様書に残す。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc17UsersRoute, sc17UsersNav } from '../index';

const USERS = [
  {
    id: 'u-tanaka',
    username: 'tanaka.taro',
    displayName: '田中 太郎',
    enabled: true,
    roles: ['platform-operator'],
    attributes: { department: 'finance', clearance: 'internal' },
  },
  {
    id: 'u-sato',
    username: 'sato.hanako',
    displayName: '佐藤 花子',
    enabled: true,
    roles: ['platform-admin'],
    attributes: { department: 'engineering', clearance: 'restricted' },
  },
  {
    id: 'u-takahashi',
    username: 'takahashi.jiro',
    displayName: '高橋 次郎',
    enabled: false,
    roles: ['platform-operator'],
    attributes: { department: 'hr', clearance: 'public' },
  },
];

const ROLES = ['platform-admin', 'platform-operator'];

const ATTRIBUTES = [
  {
    id: 'a1',
    key: 'department',
    label: '部門',
    allowedValues: ['engineering', 'finance', 'hr'],
    required: false,
    scope: 'user',
  },
  {
    id: 'a2',
    key: 'clearance',
    label: '機密区分上限',
    allowedValues: ['public', 'internal', 'confidential', 'restricted'],
    required: false,
    scope: 'user',
  },
  // 計画の「タグ」に当たる任意属性。**必須にしない**ことを測る。
  {
    id: 'a3',
    key: 'tags',
    label: 'タグ',
    allowedValues: ['経営', '経理'],
    required: false,
    scope: 'user',
  },
  // 🔴 **文書スコープの属性は主体へ割り当てない**（意味が反転する）。選択肢に出ないことを測る。
  {
    id: 'a4',
    key: 'doc_scope',
    label: '文書区分',
    allowedValues: ['private-note'],
    required: false,
    scope: 'document',
  },
];

/** 経路ごとに応答を振り分ける（1 画面が 3 本引くため、URL で分けないと取り違える）。 */
function mockApi(overrides: { users?: unknown } = {}) {
  mocks.apiRequest.mockImplementation((path: string) => {
    if (String(path).includes('/admin/users/assignable-roles')) {
      return Promise.resolve(jsonResponse(ROLES));
    }
    if (String(path).includes('/authz/attributes'))
      return Promise.resolve(jsonResponse(ATTRIBUTES));
    if (String(path).includes('/admin/users')) {
      return Promise.resolve(jsonResponse(overrides.users ?? USERS));
    }
    return Promise.resolve(jsonResponse([]));
  });
}

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc17UsersRoute(shell)], {
    initialEntry: '/admin/users',
    roles,
  });
}

/**
 * 一覧の `<table>` 要素そのもの。
 *
 * **クエリ済みのスコープを返さない** —— `within(...)` を包んだ値を関数から返すと
 * `testing-library/prefer-screen-queries` が静的に追えず error になる。呼び出し側で包む。
 */
const usersTable = () => screen.findByRole('table', { name: '利用者アカウントの一覧' });

async function openEditor(user: ReturnType<typeof userEvent.setup>, rowName: string) {
  const rows = within(await usersTable()).getAllByRole('row');
  const target = rows.find((row) => within(row).queryByText(rowName));
  await user.click(within(target!).getByRole('button', { name: '編集' }));
  return screen.getByTestId('permission-editor');
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('UserAccountManagementPage (SC-17)', () => {
  // 05_screens §SC-17 主要素 1: 利用者一覧（部門・ロール・ABAC 属性・状態）。
  it('lists users with department, roles, ABAC attributes and state', async () => {
    mockApi();
    await renderPage();

    expect(
      await screen.findByRole('heading', { name: 'ユーザーアカウント管理' }),
    ).toBeInTheDocument();

    const rows = within(await usersTable());
    expect(rows.getByText('田中 太郎')).toBeInTheDocument();
    expect(rows.getByText('finance')).toBeInTheDocument();
    // 併任は「・」で連ねる（計画の例「管理者・運用者」）。単独ロールの利用者は 2 人居る。
    expect(rows.getAllByText('platform-operator', { selector: 'td' })).toHaveLength(2);
    expect(rows.getByText('clearance: internal')).toBeInTheDocument();
    // INDEX 決定 21: 状態は色 ＋ アイコン ＋ テキスト。無効の文言は計画そのまま。
    expect(rows.getAllByText('有効')).toHaveLength(2);
    expect(rows.getByText('無効（全セッション失効）')).toBeInTheDocument();
  });

  // 05_screens §SC-17 主要素 1:「部門／ロールのフィルタ」。
  it('filters the list by department and by role', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await usersTable();

    await user.selectOptions(screen.getByLabelText('部門'), 'hr');
    await waitFor(async () =>
      expect(within(await usersTable()).queryByText('田中 太郎')).not.toBeInTheDocument(),
    );
    expect(within(await usersTable()).getByText('高橋 次郎')).toBeInTheDocument();

    // 部門を戻し、ロールで絞る（陽性対照つき: 絞る前は 3 人居る）。
    await user.selectOptions(screen.getByLabelText('部門'), '');
    expect(within(await usersTable()).getAllByRole('row')).toHaveLength(4); // 見出し行 ＋ 3
    await user.selectOptions(screen.getByLabelText('ロール'), 'platform-admin');
    await waitFor(async () =>
      expect(within(await usersTable()).getAllByRole('row')).toHaveLength(2),
    );
    expect(within(await usersTable()).getByText('佐藤 花子')).toBeInTheDocument();
  });

  // 05_screens §SC-17: 権限編集（ロール併任可・部門・機密区分上限）。保存は 2 経路へ反映する。
  it('saves the role assignment and the ABAC attributes', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');

    // 併任（複数選択）ができる。
    await user.click(screen.getByRole('checkbox', { name: 'platform-admin' }));
    await user.selectOptions(screen.getByLabelText('機密区分上限 *'), 'confidential');
    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/admin/users/u-tanaka/roles') &&
            (init as RequestInit)?.method === 'PUT' &&
            String((init as RequestInit)?.body).includes('platform-admin'),
        ),
      ).toBe(true),
    );
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/admin/users/u-tanaka/attributes') &&
            String((init as RequestInit)?.body).includes('confidential'),
        ),
      ).toBe(true),
    );
  });

  // 05_screens §SC-17 入力/バリデーション: ロール割当は**必須**（複数選択）。
  it('refuses to save an empty role assignment', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');

    await user.click(screen.getByRole('checkbox', { name: 'platform-operator' })); // 唯一のロールを外す
    const before = mocks.apiRequest.mock.calls.length;
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByTestId('assignment-issues')).toHaveTextContent(
      'ロールは 1 件以上を割り当ててください',
    );
    expect(mocks.apiRequest.mock.calls.length).toBe(before);
  });

  // 05_screens §SC-17: 部門・機密区分上限は**必須**。
  it('refuses to save when a required attribute is unset', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');

    await user.selectOptions(screen.getByLabelText('部門 *'), '');
    const before = mocks.apiRequest.mock.calls.length;
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByTestId('assignment-issues')).toHaveTextContent(
      '部門と機密区分上限は必須です。',
    );
    expect(mocks.apiRequest.mock.calls.length).toBe(before);
  });

  // 🔴 05_screens §SC-17: **タグは任意**（過剰拒否の否定側）。タグ未設定でも保存できる。
  it('does not require the optional tag attribute', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');

    // 陽性対照: 任意属性の入力欄は在り、既定は未選択である。
    expect(screen.getByLabelText('タグ')).toHaveValue('');

    await user.click(screen.getByRole('button', { name: '保存' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(([path]) =>
          String(path).endsWith('/admin/users/u-tanaka/attributes'),
        ),
      ).toBe(true),
    );
    expect(screen.queryByTestId('assignment-issues')).not.toBeInTheDocument();
  });

  // 05_screens §SC-17: 値は SC-09 の属性体系に定義済みのものだけ。**文書スコープは混ざらない。**
  it('offers only the subject-scoped dictionary entries and their allowed values', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');

    const clearance = screen.getByLabelText('機密区分上限 *');
    expect(within(clearance).getByRole('option', { name: 'restricted' })).toBeInTheDocument();
    // 別属性の許可値は混ざらない。
    expect(within(clearance).queryByRole('option', { name: 'finance' })).not.toBeInTheDocument();
    // 文書スコープの属性は出さない。
    expect(screen.queryByLabelText('文書区分')).not.toBeInTheDocument();
    // ロールも焼き込まず認可基盤から引く（値域外は出ない）。
    expect(screen.queryByRole('checkbox', { name: 'wiki-editor' })).not.toBeInTheDocument();
  });

  // 05_screens §SC-17 アクション: 無効化→全セッション即時失効。無効な利用者には再有効化を出す。
  it('sends a disable request for an enabled user and an enable request for a disabled one', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();

    await openEditor(user, '田中 太郎');
    await user.click(screen.getByRole('button', { name: '無効化（全セッション失効）' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) =>
            String(path).endsWith('/admin/users/u-tanaka/disable') &&
            (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );

    await user.click(screen.getByRole('button', { name: '閉じる' }));
    await openEditor(user, '高橋 次郎');
    await user.click(screen.getByRole('button', { name: '再有効化' }));
    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(([path]) =>
          String(path).endsWith('/admin/users/u-takahashi/enable'),
        ),
      ).toBe(true),
    );
  });

  // 🔴 05_screens §SC-17 アクション:「**本画面から新規作成はしない**」。
  // **先に「操作可能な要素が在る」ことを確かめてから測る**（何も描かない実装でも緑にしない）。
  it('offers no way to create a user and says where accounts come from', async () => {
    mockApi();
    await renderPage();
    await usersTable();

    // 陽性対照: 画面には操作可能な要素が在る。
    expect(screen.getAllByRole('button', { name: '編集' }).length).toBeGreaterThan(0);
    expect(screen.getByLabelText('部門')).toBeInTheDocument();

    // 否定形: 作成に相当する操作が 1 つも無い。
    for (const name of ['新規作成', '追加', 'ユーザーを追加', '招待', '登録']) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
    expect(screen.queryByLabelText('ユーザー名')).not.toBeInTheDocument();
    expect(screen.getByTestId('users-help')).toHaveTextContent('この画面からは作成できません');
  });

  // IADR-0009 / IADR-0035: 権限外には画面の存在を示さない（RequireRole → NotFound）。
  it('hides the screen from non-admins (existence hiding)', async () => {
    mockApi();
    await renderPage(['platform-operator']);

    await waitFor(() =>
      expect(
        screen.queryByRole('heading', { name: 'ユーザーアカウント管理' }),
      ).not.toBeInTheDocument(),
    );
    // 陽性対照は上のテスト群（管理者では見出しが出る）。ここでは一覧も引かないことまで測る。
    expect(
      mocks.apiRequest.mock.calls.some(([path]) => String(path).includes('/admin/users')),
    ).toBe(false);
  });

  // 🔴 取得失敗を空の一覧へ潰さない（「1 人も居ない」と「引けない」は別の意味である）。
  it('surfaces a fetch failure instead of degrading to an empty list', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'failed', 500, []));
    await renderPage();

    expect(await screen.findByTestId('users-error')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  // 後段の拒否理由（RFC7807）をそのまま出す。中立化すると管理者が直せなくなる。
  it('shows the downstream rejection reason for an out-of-dictionary value', async () => {
    mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
      if (init?.method === 'PUT' && String(path).includes('/attributes')) {
        return Promise.reject(
          new ApiError('validation', 'bad request', 400, [
            "属性 'clearance' の値 'top-secret' は許可値に含まれません。",
          ]),
        );
      }
      if (String(path).includes('/admin/users/assignable-roles')) {
        return Promise.resolve(jsonResponse(ROLES));
      }
      if (String(path).includes('/authz/attributes'))
        return Promise.resolve(jsonResponse(ATTRIBUTES));
      return Promise.resolve(jsonResponse(USERS));
    });
    const user = userEvent.setup();
    await renderPage();
    await openEditor(user, '田中 太郎');
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByTestId('assignment-error')).toHaveTextContent('top-secret');
  });

  // 05_screens §共通シェル: 左ナビ「管理」グループ・システム管理者限定。
  it('declares the nav item under the admin group for administrators only', () => {
    expect(sc17UsersNav.to).toBe('/admin/users');
    expect(sc17UsersNav.group).toBe('admin');
    expect(sc17UsersNav.requiresAnyRole).toEqual(['platform-admin']);
  });
});
