import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）の再実装（#504）。
// 属性辞書・ポリシー定義・検証結果と、**着手保留（辺の型）・契約の不在（タグ辞書・検証ボタン）が
// 画面に現れないこと**を固定する。
const mocks = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiFetch: mocks.apiFetch,
}));

import { createSc09AdminAbacRoute, sc09AdminAbacNav } from './index';

const ATTRIBUTES = [
  {
    id: 'a1',
    key: 'dept',
    label: '部門',
    allowedValues: ['経理', '開発'],
    required: true,
    scope: 'user',
  },
  {
    id: 'a2',
    key: 'confidentiality',
    label: '機密区分',
    allowedValues: ['public', 'internal'],
    required: true,
    scope: 'document',
  },
];

const POLICIES = [
  {
    id: 'p1',
    name: 'P-012 経理文書',
    action: 'read',
    userConditions: { dept: ['経理'] },
    documentConditions: { confidentiality: ['internal'] },
    isActive: true,
  },
  {
    id: 'p2',
    name: 'P-013 役員限定',
    action: 'manage',
    userConditions: {},
    documentConditions: {},
    isActive: false,
  },
];

/** 一覧 2 本（属性・ポリシー）に応答を与え、書き込みは既定で成功させる。 */
function mockApi(
  overrides: {
    attributes?: unknown;
    policies?: unknown;
    write?: () => Promise<unknown>;
  } = {},
) {
  mocks.apiFetch.mockImplementation(async (path: string, req?: { method?: string }) => {
    const pick = (value: unknown) => {
      if (value instanceof Error) throw value;
      return value;
    };
    if (req?.method && req.method !== 'GET') {
      return overrides.write ? overrides.write() : undefined;
    }
    if (path === '/admin/authz/attributes') return pick(overrides.attributes ?? ATTRIBUTES);
    return pick(overrides.policies ?? POLICIES);
  });
}

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc09AdminAbacRoute(shell)], {
    initialEntry: '/admin/abac',
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

describe('AdminAbacSettingsPage (SC-09)', () => {
  // hi-fi 417 は「ポリシー定義」を選択中に描く。既定タブをそこへ合わせる。
  it('opens on the policy tab and lists the policies with their conditions', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('heading', { name: '管理者設定（ABAC）' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'ポリシー定義' })).toHaveAttribute(
      'aria-selected',
      'true',
    );

    const table = within(await screen.findByRole('table', { name: 'アクセスポリシーの一覧' }));
    expect(table.getByText('P-012 経理文書')).toBeInTheDocument();
    // 条件は「属性キー → 許可値の集合所属」として要約する（契約が表現できる範囲）。
    expect(table.getByText('利用者 dept = 経理')).toBeInTheDocument();
    expect(table.getByText('文書 confidentiality = internal')).toBeInTheDocument();
    // 条件を持たないポリシーは「条件なし」と明示する（空欄にしない）。
    expect(table.getByText('条件なし（すべてに一致）')).toBeInTheDocument();
    // INDEX 決定 21: 状態は色 ＋ アイコン ＋ テキスト。
    expect(table.getByText('有効')).toBeInTheDocument();
    expect(table.getByText('無効')).toBeInTheDocument();
  });

  // 計画 §SC-09 §主要素「属性体系エディタ」。モックは中身を描いていないため実装側で補う。
  it('lists the attribute dictionary on the attribute tab', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    await user.click(screen.getByRole('tab', { name: '属性体系' }));

    const table = within(await screen.findByRole('table', { name: '属性辞書の一覧' }));
    expect(table.getByText('dept')).toBeInTheDocument();
    expect(table.getByText('経理 / 開発')).toBeInTheDocument();
    expect(table.getByText('利用者')).toBeInTheDocument();
    expect(table.getByText('文書')).toBeInTheDocument();
  });

  it('creates an attribute with the parsed allowed values', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await user.click(screen.getByRole('tab', { name: '属性体系' }));
    await screen.findByRole('table', { name: '属性辞書の一覧' });

    await user.type(screen.getByLabelText('キー（必須）'), 'grade');
    await user.type(screen.getByLabelText('ラベル'), '職位');
    await user.type(screen.getByLabelText('許可値（カンマ区切り）'), ' 一般 , 役員 ,');
    await user.selectOptions(screen.getByLabelText('スコープ'), 'user');
    await user.click(screen.getByRole('button', { name: '追加する' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/authz/attributes', {
        method: 'POST',
        json: {
          key: 'grade',
          label: '職位',
          allowedValues: ['一般', '役員'],
          required: false,
          scope: 'user',
        },
      }),
    );
  });

  // IADR-0006: 参照中の属性は 409 で削除を拒否される。理由を書かないと「なぜ消えないか」が分からない。
  // IADR-0040: サーバは**参照元のポリシー名**を Problem 本文に載せる（AuthzEndpoints.cs の
  // `Results.Problem(detail: "属性 '…' は次のポリシーが参照しているため削除できません: …")`）。
  // fixture は**実サーバ応答の形**（details 非空）を再現し、詳細が消えないことを固定する。
  it('explains a 409 when deleting a referenced attribute and keeps the server detail', async () => {
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [
            "属性 'dept' (scope=user) は次のポリシーが参照しているため削除できません: P-012 経理文書",
          ]),
        ),
    });
    const user = userEvent.setup();
    await renderPage();
    await user.click(screen.getByRole('tab', { name: '属性体系' }));
    await screen.findByRole('table', { name: '属性辞書の一覧' });

    await user.click(screen.getByRole('button', { name: '属性を削除: dept' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'この属性は使用中のため削除できません。',
    );
    // **どのポリシーが参照しているか**が消えていない（固定文言で潰さない）。
    expect(screen.getByRole('alert')).toHaveTextContent('P-012 経理文書');
    // 409 は障害ではなく拒否である。tone に合わせてラベルも「注意」にする（INDEX 決定 21 の敷衍）。
    expect(screen.getByRole('alert')).toHaveTextContent('注意');
  });

  // 計画の入力表「対象属性｜必須｜選択｜**定義済み属性のみ**」。属性辞書から引く。
  it('builds a policy condition from the defined attributes only', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    // 選択肢は属性辞書に由来する（自由入力の余地が無い）。
    const attributeSelect = screen.getByLabelText('対象属性');
    expect(within(attributeSelect).getByRole('option', { name: '部門（利用者）' })).toBeInTheDocument();

    await user.type(screen.getByLabelText('名前（必須）'), 'P-014 開発設計');
    await user.selectOptions(attributeSelect, 'dept');
    await user.selectOptions(screen.getByLabelText('条件の値'), '開発');
    await user.click(screen.getByRole('button', { name: '条件を追加' }));

    expect(
      within(screen.getByRole('list', { name: '設定した条件' })).getByText('利用者 dept = 開発'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiFetch).toHaveBeenCalledWith('/admin/authz/policies', {
        method: 'POST',
        json: {
          name: 'P-014 開発設計',
          action: 'read',
          userConditions: { dept: ['開発'] },
          documentConditions: {},
        },
      }),
    );
  });

  // 計画 §SC-09 §アクション: 保存前にポリシーを検証し、矛盾はエラー表示。検証結果パネルへ集約する。
  it('shows the server-side contradiction detail in the validation panel', async () => {
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError('validation', '入力内容に誤りがあります。', 400, [
            'P-013 と矛盾: 「秘」文書が grade < 役員 に到達し得る経路があります',
          ]),
        ),
    });
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    await user.type(screen.getByLabelText('名前（必須）'), 'P-014');
    await user.click(screen.getByRole('button', { name: '保存' }));

    const panel = within(screen.getByRole('region', { name: '検証結果' }));
    expect(await panel.findByRole('alert')).toHaveTextContent(
      'P-013 と矛盾: 「秘」文書が grade < 役員 に到達し得る経路があります',
    );
  });

  it('confirms in the validation panel that a saved policy takes effect immediately', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    await user.type(screen.getByLabelText('名前（必須）'), 'P-014');
    await user.click(screen.getByRole('button', { name: '保存' }));

    const panel = within(screen.getByRole('region', { name: '検証結果' }));
    expect(await panel.findByRole('status')).toHaveTextContent(
      'ポリシーを保存しました。認可判定へ即時反映されます。',
    );
  });

  // 名前が空のあいだは保存できない（hi-fi 430 も保存ボタンを無効に描く）。
  it('refuses to save until the policy name is filled', async () => {
    mockApi();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    expect(screen.getByRole('button', { name: '保存' })).toBeDisabled();
  });

  // 更新系の成功後は一覧を取り直す（手書きの再取得を持たない。IADR-0127 決定 5 と同じ作法）。
  it('refetches the policy list after a successful toggle', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });
    const before = mocks.apiFetch.mock.calls.filter(
      ([path]) => path === '/admin/authz/policies',
    ).length;

    await user.click(screen.getByRole('button', { name: 'ポリシーを無効化: P-012 経理文書' }));

    await waitFor(() =>
      expect(
        mocks.apiFetch.mock.calls.filter(([path]) => path === '/admin/authz/policies').length,
      ).toBe(before + 1),
    );
  });

  // #503 の M28 / M29 の教訓: **操作を跨いだ状態**を見る。前の操作の失敗バナーが残ってはいけない。
  // **異なるミューテーションを跨ぐ**ことが要点である——同じミューテーションを 2 回動かすと
  // TanStack Query が自前で状態を入れ替えるため、`beginOperation()` を外しても落ちない
  // （本 issue の変異試験で実測した。削除 → 削除では素通りし、削除 → 追加で落ちる）。
  it('shows only the latest operation result across different mutations', async () => {
    mockApi({
      write: () =>
        // 削除は 409 で失敗する。追加（POST）は成功させたいので、パスで分けずに
        // 「1 回目だけ失敗」ではなく **削除だけ失敗**にする（操作の種類で分ける）。
        Promise.reject(new ApiError('conflict', '競合が発生しました。', 409)),
    });
    const user = userEvent.setup();
    await renderPage();
    await user.click(screen.getByRole('tab', { name: '属性体系' }));
    await screen.findByRole('table', { name: '属性辞書の一覧' });

    // 1 つ目のミューテーション（削除）は失敗する。
    await user.click(screen.getByRole('button', { name: '属性を削除: dept' }));
    expect(await screen.findByRole('alert')).toBeInTheDocument();

    // 2 つ目のミューテーション（追加）は成功する。古い失敗が残っていてはいけない。
    mockApi({ write: () => Promise.resolve(undefined) });
    await user.type(screen.getByLabelText('キー（必須）'), 'grade');
    await user.click(screen.getByRole('button', { name: '追加する' }));

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(await screen.findByRole('status')).toHaveTextContent('属性辞書を更新しました。');
  });

  // 同じ形をポリシー側でも見る（こちらは 3 本のミューテーションがある）。
  it('clears a stale validation error when another policy operation succeeds', async () => {
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError('validation', '入力内容に誤りがあります。', 400, ['P-013 と矛盾']),
        ),
    });
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    // 1 つ目のミューテーション（保存）は 400 で失敗する。
    await user.type(screen.getByLabelText('名前（必須）'), 'P-014');
    await user.click(screen.getByRole('button', { name: '保存' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('P-013 と矛盾');

    // 2 つ目のミューテーション（有効／無効の切替）は成功する。
    mockApi({ write: () => Promise.resolve(undefined) });
    await user.click(screen.getByRole('button', { name: 'ポリシーを無効化: P-012 経理文書' }));

    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  // BFF の後段障害を「0 件」へ縮退させない（管理者が「設定が消えた」と誤読する）。
  it('shows an error instead of an empty list when the query fails', async () => {
    mockApi({ policies: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    await renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('サーバでエラーが発生しました。');
    expect(screen.queryByText('ポリシーは登録されていません。')).not.toBeInTheDocument();
  });

  // IADR-0119: 「辺の型」は**着手保留の要求**に属する（どの要求かは IADR-0119 と画面仕様書が持つ。
  // **保留対象の ID をここへ書くと check-test-traceability.js が「実装が先行している」と
  //  誤って報告する**——その ID は、当該機能に着手する issue が初めて書く）。
  // **まず「見えるはずの条件」で描画されていることを確かめてから**無いことを assert する
  // （#502 の M3 の教訓）。
  it('does not render the edge-type dictionary (its requirement is on hold)', async () => {
    mockApi();
    await renderPage();

    // 見えるはずのもの（2 つの区画）が在ることを先に確かめる。
    expect(await screen.findByRole('tab', { name: '属性体系' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'ポリシー定義' })).toBeInTheDocument();

    expect(screen.queryByRole('tab', { name: '辺の型' })).not.toBeInTheDocument();
    expect(screen.queryByText(/辺の型/)).not.toBeInTheDocument();
    expect(screen.queryByText(/逆向きの表示語/)).not.toBeInTheDocument();
    expect(screen.queryByText(/使用件数/)).not.toBeInTheDocument();
  });

  // 契約の不在: タグ辞書（値集合・使用件数・改名の追随）と dry-run の検証ボタン。
  it('does not render the tag dictionary or a dry-run validate button (no contract behind them)', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('tab', { name: 'ポリシー定義' })).toBeInTheDocument();

    expect(screen.queryByRole('tab', { name: 'タグ辞書' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '検証' })).not.toBeInTheDocument();
  });

  // 遷移先の画面（MCP クライアント管理）が未実装（#445 待ち）のリンクを置かない——押すと
  // NotFound が出て、権限による秘匿と未実装が区別できなくなる。
  it('does not link to the MCP client screen while it does not exist', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('tab', { name: 'ポリシー定義' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /MCP/ })).not.toBeInTheDocument();
  });

  // ADR-0031: 文言は Lingui のカタログ（ja / en）。
  it('renders in English when the en locale is active', async () => {
    mockApi();
    await renderPage();
    await screen.findByRole('heading', { name: '管理者設定（ABAC）' });

    act(() => {
      activate('en');
    });

    expect(
      await screen.findByRole('heading', { name: 'Administrator settings (ABAC)' }),
    ).toBeInTheDocument();
  });
});

// IADR-0009 / IADR-0035 / IADR-0040: 存在秘匿。SC-09 は platform-admin のみ（operator も不可）。
describe('SC-09 access control (#504)', () => {
  it('grants access to platform-admin', async () => {
    mockApi();
    await renderPage(['platform-admin']);
    expect(await screen.findByRole('heading', { name: '管理者設定（ABAC）' })).toBeInTheDocument();
  });

  it('hides existence (NotFound) for an operator and for a plain user', async () => {
    mockApi();
    for (const roles of [['platform-operator'], ['user']]) {
      const view = await renderPage(roles);
      expect(
        await screen.findByRole('heading', { name: '見つかりませんでした' }),
      ).toBeInTheDocument();
      expect(
        screen.queryByRole('heading', { name: '管理者設定（ABAC）' }),
      ).not.toBeInTheDocument();
      // 権限外では ABAC 管理 API を呼ばない（要求の有無から存在を推測させない）。
      expect(mocks.apiFetch).not.toHaveBeenCalled();
      view.unmount();
    }
  });

  it('produces the same not-found markup as a plain absence', async () => {
    const { NotFound } = await import('@foundation/ui/NotFound');
    const { render } = await import('@testing-library/react');

    mockApi();
    await renderPage(['user']);
    const forbidden = (await screen.findByRole('heading', { name: '見つかりませんでした' }))
      .parentElement?.outerHTML;

    const absent = render(<NotFound />);
    expect(forbidden).toBeTruthy();
    expect(forbidden).toBe(absent.container.firstElementChild?.outerHTML);
  });

  it('exposes a nav entry limited to the admin role in the admin group', () => {
    expect(sc09AdminAbacNav.requiresAnyRole).toEqual(['platform-admin']);
    // 05_screens §共通シェル: SC-09 は「管理」グループ。
    expect(sc09AdminAbacNav.group).toBe('admin');
  });
});
