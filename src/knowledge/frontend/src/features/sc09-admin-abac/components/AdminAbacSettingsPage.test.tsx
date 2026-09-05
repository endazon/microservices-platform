import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse, noContent } from '@foundation/testing/bffResponse';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）の再実装（#504）。
// 属性辞書・ポリシー定義・検証結果・**タグ辞書**（#640）と、
// **着手保留（辺の型）が画面に現れないこと**を固定する。
//
// **［2026-08-09 / #640］「契約の不在」で描かなかった 2 件はどちらも解消した**
// （検証ボタン = #535、タグ辞書 = #640）。不在を固定していたテストは**反転させてある**——
// [[IADR-0129]] 決定 1 が実装しない理由を記録している以上、理由が消えた証跡が要る。
//
// IADR-0135 決定 4（#519）: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、
// モックは `apiRequest` に当てる（`apiFetch` を差し替えても効かない）。
// **400 / 409 の Problem 詳細（`ApiError.details`）は載せ替えの影響を受けない**——非 2xx を投げるのは
// どちらの経路でも `apiRequest` だからである（IADR-0040 決定 2 の表示がそのまま通り続ける）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc09AdminAbacRoute, sc09AdminAbacNav } from '../routes/sc09AdminAbacRoute';

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

// FR-09, SC-09, #640: タグ辞書（値集合 ＋ 使用件数）。
const TAGS = {
  tags: [
    { id: '11111111-2222-3333-4444-555555555555', name: '経理', usageCount: 3 },
    { id: '22222222-3333-4444-5555-666666666666', name: '規程', usageCount: 0 },
  ],
};

// FR-17, SC-09, #1241: 辺の型辞書（値集合 ＋ 使用件数）。
// **使用件数が 1 以上の行と 0 の行を 1 本ずつ置く** —— 削除拒否と削除成功の両方を測るため。
const EDGE_TYPES = [
  {
    id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
    name: 'cites',
    layer: 'core',
    isSymmetric: false,
    isSeed: true,
    usageCount: 342,
  },
  {
    id: 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff',
    name: 'part-of',
    layer: 'recommended',
    isSymmetric: false,
    isSeed: false,
    usageCount: 0,
  },
];

/** 一覧 4 本（属性・ポリシー・タグ辞書・辺の型辞書）に応答を与え、書き込みは既定で成功させる。 */
function mockApi(
  overrides: {
    attributes?: unknown;
    policies?: unknown;
    tags?: unknown;
    edgeTypes?: unknown;
    write?: () => Promise<unknown>;
  } = {},
) {
  mocks.apiRequest.mockImplementation(async (path: string, init?: RequestInit) => {
    const pick = (value: unknown) => {
      if (value instanceof Error) throw value;
      return jsonResponse(value);
    };
    if (init?.method && init.method !== 'GET') {
      return overrides.write ? overrides.write() : noContent();
    }
    if (path === '/admin/authz/attributes') return pick(overrides.attributes ?? ATTRIBUTES);
    // FR-09, SC-09, #640: タグ辞書（/bff/tags）。
    if (path === '/tags') return pick(overrides.tags ?? TAGS);
    // FR-17, SC-09, #1241: 辺の型辞書（/bff/edge-types）。
    // 🔴 **`/graph/edge-types`（描画用カタログ）ではない。** 取り違えるとこのモックに当たらず、
    // ポリシー一覧の応答が返ってしまう（＝取り違えが緑になる）。
    if (path === '/edge-types') return pick(overrides.edgeTypes ?? EDGE_TYPES);
    return pick(overrides.policies ?? POLICIES);
  });
}

/** 生成コードが送った JSON 本文（直近の書き込み要求）を読む。 */
function sentBody(calls: unknown[][]): unknown {
  const write = [...calls].reverse().find(([, init]) => (init as RequestInit)?.method === 'POST');
  return JSON.parse(String((write?.[1] as RequestInit).body));
}

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc09AdminAbacRoute(shell)], {
    initialEntry: '/admin/abac',
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
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/admin/authz/attributes',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(sentBody(mocks.apiRequest.mock.calls)).toEqual({
      key: 'grade',
      label: '職位',
      allowedValues: ['一般', '役員'],
      required: false,
      scope: 'user',
    });
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
    expect(
      within(attributeSelect).getByRole('option', { name: '部門（利用者）' }),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText('名前（必須）'), 'P-014 開発設計');
    await user.selectOptions(attributeSelect, 'dept');
    await user.selectOptions(screen.getByLabelText('条件の値'), '開発');
    await user.click(screen.getByRole('button', { name: '条件を追加' }));

    expect(
      within(screen.getByRole('list', { name: '設定した条件' })).getByText('利用者 dept = 開発'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/admin/authz/policies',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(sentBody(mocks.apiRequest.mock.calls)).toEqual({
      name: 'P-014 開発設計',
      action: 'read',
      userConditions: { dept: ['開発'] },
      documentConditions: {},
    });
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
    const before = mocks.apiRequest.mock.calls.filter(
      ([path]) => path === '/admin/authz/policies',
    ).length;

    await user.click(screen.getByRole('button', { name: 'ポリシーを無効化: P-012 経理文書' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.filter(([path]) => path === '/admin/authz/policies').length,
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
    mockApi({ write: () => Promise.resolve(noContent()) });
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
    mockApi({ write: () => Promise.resolve(noContent()) });
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

  // SC-09, IADR-0135 決定 7［2026-08-06 追記］: 一覧が**本文なし**（204）で返っても画面は落ちない。
  //
  // `bffFetch` は本文が空なら `{}` を返すため、`okData(res) ?? []` では `??` が発火せず
  // `{}` がそのまま `policies.map` / `attributes.map` へ届いていた（＝ルートごとクラッシュした）。
  // `okArray` が「配列でなければ空配列」まで詰めることで、0 件表示へ縮退する。
  it('degrades to the empty list when the policy response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path === '/admin/authz/attributes') return jsonResponse(ATTRIBUTES);
      return noContent();
    });
    await renderPage();

    expect(await screen.findByText('ポリシーは登録されていません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('degrades to the empty list when the attribute response has no body (204)', async () => {
    mocks.apiRequest.mockImplementation(async (path: string) => {
      if (path === '/admin/authz/attributes') return noContent();
      return jsonResponse(POLICIES);
    });
    const user = userEvent.setup();
    await renderPage();
    await screen.findByRole('table', { name: 'アクセスポリシーの一覧' });

    await user.click(screen.getByRole('tab', { name: '属性体系' }));

    expect(await screen.findByText('属性は登録されていません。')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // ★ FR-17, SC-09, #1241: 辺の型辞書は**契約が着地したので描く**。
  //
  // 🔴 **このテストは「不在を固定する」ものから反転させた（削除ではない）。**
  // 従前は `does not render the edge-type dictionary (its requirement is on hold)` であり、
  // IADR-0129 決定 1 の理由 A（要求の着手保留）を固定していた。**その保留は 2026-08-07（#586）に
  // 解除され**、判断先の #504 が判断を残さずに閉じたまま残っていたものを本 issue が回収した。
  // **消すと「なぜ描くようになったか」が追えなくなる**（タグ辞書の反転と同じ作法）。
  //
  // **起点 ID をここに書けるようになったのも本 issue からである** —— 従前は
  // 「保留対象の ID を書くと check-test-traceability.js が『実装が先行している』と誤報する」
  // という理由で伏せていた。**着手したので、その理由は失効した。**
  it('renders the edge-type dictionary now that the contract exists', async () => {
    mockApi();
    await renderPage();

    // 見えるはずのもの（他の区画）が在ることを先に確かめる（#502 の M3 の教訓）。
    expect(await screen.findByRole('tab', { name: '属性体系' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'ポリシー定義' })).toBeInTheDocument();

    expect(screen.getByRole('tab', { name: '辺の型' })).toBeInTheDocument();
  });

  // ★ 陰性対照（#1241）: **「逆向きの表示語」の列は作らない。**
  //
  // hi-fi モックはその列を描くが、ADR-0033 が逆向きの語を定めているのは
  // **バックリンク欄での表示**であって辞書の管理項目ではなく、ドメインにも契約にもその欄が無い。
  // **そのバックリンク欄自体 SC-04 側で実現方式が未確定である**（#1240）——
  // **消費者の無い管理項目を先に作ると、計画が決めたときに実装ではなく画面が先に決めたことになる。**
  it('does not invent a reverse-label column that has no contract behind it', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));

    // 陽性対照: 契約が持つ列は在る（＝下の不在が「表がまだ描けていないだけ」ではない）。
    expect(await screen.findByRole('columnheader', { name: '型名' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: '使用件数' })).toBeInTheDocument();

    expect(screen.queryByRole('columnheader', { name: '逆向きの表示語' })).not.toBeInTheDocument();
    expect(screen.queryByText(/逆向きの表示語/)).not.toBeInTheDocument();
  });

  // ★ #535: dry-run の検証ボタンは**契約が着地したので描く**（裁定 Q23）。
  //
  // **［2026-08-09 / #640］タグ辞書も描くようになった**（BFF の書き込み口が着地したため）。
  // **1 つの `it` が 2 つの不在をまとめて主張していたので分けてあった**——
  // 分けていたおかげで、片方の契約が着地しても、もう片方を巻き込まずに反転できた。
  it('renders the dry-run validate button now that the contract exists', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('tab', { name: 'ポリシー定義' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '検証' })).toBeInTheDocument();
  });

  // ★ #535: **検証は保存しない。** 押した先が `/admin/authz/policies/validate` であり、
  // 登録の口（`/admin/authz/policies`）は呼ばれないこと。
  //
  // **これを取り違えると「試しに検証したら壊れた」という最悪の結果になる。**
  it('validates without saving, and says so', async () => {
    mocks.apiRequest.mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method && init.method !== 'GET') {
        if (path === '/admin/authz/policies/validate') {
          return jsonResponse({ valid: true, errors: [] });
        }
        return noContent();
      }
      if (path === '/admin/authz/attributes') return jsonResponse(ATTRIBUTES);
      return jsonResponse(POLICIES);
    });
    await renderPage();

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('名前（必須）'), '検証だけ');
    await user.click(screen.getByRole('button', { name: '検証' }));

    expect(await screen.findByText('矛盾はありません。まだ保存していません。')).toBeInTheDocument();

    const writes = mocks.apiRequest.mock.calls.filter(
      ([, init]) => (init as RequestInit | undefined)?.method === 'POST',
    );
    expect(writes.map(([path]) => path)).toEqual(['/admin/authz/policies/validate']);
  });

  // #535: 矛盾は**エラーではなく検証結果**として出る（200 ＋ `valid: false`）。
  it('shows the contradictions the dry-run found', async () => {
    mocks.apiRequest.mockImplementation(async (path: string, init?: RequestInit) => {
      if (init?.method && init.method !== 'GET') {
        if (path === '/admin/authz/policies/validate') {
          return jsonResponse({
            valid: false,
            errors: ['documentConditions.dept に辞書外の値があります: 総務'],
          });
        }
        return noContent();
      }
      if (path === '/admin/authz/attributes') return jsonResponse(ATTRIBUTES);
      return jsonResponse(POLICIES);
    });
    await renderPage();

    const user = userEvent.setup();
    await user.type(await screen.findByLabelText('名前（必須）'), '矛盾あり');
    await user.click(screen.getByRole('button', { name: '検証' }));

    expect(
      await screen.findByText('documentConditions.dept に辞書外の値があります: 総務'),
    ).toBeInTheDocument();
  });

  // ★ #640: タグ辞書は**契約が着地したので描く**。
  //
  // **このテストは「不在を固定する」ものから反転させた**（削除ではない）——
  // IADR-0129 決定 1 が理由 B（契約の不在）として実装しないと記録しており、
  // **その理由が消えたことを示す証跡が要る**。消すと「なぜ描くようになったか」が追えなくなる。
  it('renders the tag dictionary now that the BFF write contract exists', async () => {
    mockApi();
    await renderPage();

    expect(await screen.findByRole('tab', { name: 'タグ辞書' })).toBeInTheDocument();
  });

  // FR-09, SC-09, #640: 一覧は**使用件数つき**で出る（IADR-0152 決定 2）。
  it('lists dictionary tags with their usage counts', async () => {
    mockApi();
    await renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));

    expect(await screen.findByText('経理')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  // ★ SC-09「削除前に使用件数を示す」。**件数が画面に出ることを固定する。**
  //
  // **`toMessages` の既定文言では代わりにならない** —— サーバの 409 本文は
  // `{ error, message, usageCount }` であり、`parseProblemDetails` が `message` を
  // 読むまでは `details` が空で「競合が発生しました。」しか出せなかった（#640 で実測）。
  // **件数は翻訳済みの文へ数値として差し込む**（サーバの日本語をそのまま出すと en で混ざる）。
  it('shows the usage count when a tag deletion is refused', async () => {
    // **`ApiError.body` に本文を載せる**（#640 で追加）。`details` は文字列しか持てず、
    // **翻訳済みの文へ数値を差し込めない**ためである。
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError(
            'conflict',
            '競合が発生しました。',
            409,
            ['タグ「経理」は 3 件の文書で使われているため削除できません。'],
            { error: 'tag_in_use', usageCount: 3 },
          ),
        ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.click(await screen.findByRole('button', { name: 'タグを削除: 経理' }));

    // 件数が**翻訳済みの文の中に**出る（サーバの日本語をそのまま流していない）。
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'このタグは 3 件の文書で使われているため削除できません。',
    );
    // 409 は障害ではなく拒否である。ラベルも「注意」にする（INDEX 決定 21 の敷衍）。
    expect(screen.getByRole('alert')).toHaveTextContent('注意');
  });

  // FR-09, SC-09, #640（受け入れ基準 1）: 画面から**追加**できる。
  it('adds a tag to the dictionary from the screen', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.type(screen.getByLabelText('タグ名（必須）'), '新規タグ');
    await user.click(screen.getByRole('button', { name: '追加' }));

    // **手書き HTTP クライアントを使っていない**（生成フック経由で /tags へ POST される）。
    const post = [...mocks.apiRequest.mock.calls]
      .reverse()
      .find(([, init]) => (init as RequestInit)?.method === 'POST');
    expect(post?.[0]).toBe('/tags');
    expect(JSON.parse(String((post?.[1] as RequestInit).body))).toEqual({ name: '新規タグ' });
  });

  // FR-09, SC-09, #640（受け入れ基準 2）: 画面から**改名**できる。
  //
  // **文書は 1 件も書き換わらない**（IADR-0153 決定 1）——正本が識別子を参照しているためで、
  // 画面は名前だけを送る。ここで固定するのは「改名の口を叩けること」である。
  it('renames a dictionary tag from the screen', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.click(await screen.findByRole('button', { name: 'タグを改名: 経理' }));

    const field = screen.getByLabelText('タグの新しい名前: 経理');
    await user.clear(field);
    await user.type(field, '経理部');
    await user.click(screen.getByRole('button', { name: '保存' }));

    const put = [...mocks.apiRequest.mock.calls]
      .reverse()
      .find(([, init]) => (init as RequestInit)?.method === 'PUT');
    expect(String(put?.[0])).toContain('/tags/');
    expect(JSON.parse(String((put?.[1] as RequestInit).body))).toEqual({ name: '経理部' });
  });

  // ★ FR-09, SC-09, #640: 改名の**波及件数**を出す（[[IADR-0153]] 決定 3）。
  //
  // **射影（Qdrant / Wiki.js）の反映は非同期である。** 件数が無いと管理者は
  // **「対象が 0 件だった」と「まだ届いていない」を区別できない**。
  // 削除拒否では件数を見せて行動させているので、**同じ非同期の波及を扱う面として揃える**。
  it('shows how many documents a rename propagated to', async () => {
    mockApi({
      write: () =>
        Promise.resolve(
          jsonResponse({
            tag: { id: '11111111-2222-3333-4444-555555555555', name: '経理部', usageCount: 7 },
            republishedDocuments: 7,
          }),
        ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.click(await screen.findByRole('button', { name: 'タグを改名: 経理' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByRole('status')).toHaveTextContent(
      'タグを改名しました。7 件の文書へ反映しています。',
    );
  });

  // ★ [[IADR-0127]] 決定 7: **画面が出す操作結果は直近の 1 件だけ**である。
  //
  // TanStack Query の `isError` は**そのミューテーション自身が再度 mutate されるまで消えない**。
  // 放置すると「削除が 409 で拒否された直後に別のタグを改名して成功しても、
  // **古い削除拒否の警告が残り、改名成功の通知が出ない**」という状態になる。
  // 管理者は「今の改名が失敗した」と誤解する。
  //
  // `AttributeDictionaryPanel` は同じ決定に従って `beginOperation()` を持っている。
  it('drops a stale failure when a different operation succeeds', async () => {
    let mode: 'conflict' | 'ok' = 'conflict';
    mockApi({
      write: () =>
        mode === 'conflict'
          ? Promise.reject(
              new ApiError('conflict', '競合が発生しました。', 409, [], {
                error: 'tag_in_use',
                usageCount: 3,
              }),
            )
          : Promise.resolve(
              jsonResponse({
                tag: { id: '11111111-2222-3333-4444-555555555555', name: '経理部', usageCount: 3 },
                republishedDocuments: 3,
              }),
            ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));

    // 1. 使用中のタグを消そうとして 409 で拒否される。
    await user.click(await screen.findByRole('button', { name: 'タグを削除: 経理' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('3 件の文書で使われている');

    // 2. 削除を諦め、**別の操作（改名）を成功させる**。
    mode = 'ok';
    await user.click(await screen.findByRole('button', { name: 'タグを改名: 経理' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    // 3. **古い削除拒否は消え、改名成功が出る。**
    expect(await screen.findByRole('status')).toHaveTextContent('タグを改名しました。');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 改名はキャンセルできる（**送らない**）。編集欄を開いただけで書き込みが走ってはならない。
  it('does not write anything when a rename is cancelled', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.click(await screen.findByRole('button', { name: 'タグを改名: 経理' }));
    await user.click(screen.getByRole('button', { name: 'キャンセル' }));

    expect(
      mocks.apiRequest.mock.calls.some(([, init]) => (init as RequestInit)?.method === 'PUT'),
    ).toBe(false);
    expect(await screen.findByText('経理')).toBeInTheDocument();
  });

  // 使用件数 0 のタグは**削除できる**（SC-09。拒否されるのは参照 1 件以上のときだけ）。
  it('deletes an unused dictionary tag', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.click(await screen.findByRole('button', { name: 'タグを削除: 規程' }));

    const del = [...mocks.apiRequest.mock.calls]
      .reverse()
      .find(([, init]) => (init as RequestInit)?.method === 'DELETE');
    expect(String(del?.[0])).toContain('/tags/');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 辞書が空のときは**行を描かず**、その旨を出す（0 件と読み込み中を区別する）。
  it('says so when the dictionary is empty', async () => {
    mockApi({ tags: { tags: [] } });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));

    expect(await screen.findByText('タグは登録されていません。')).toBeInTheDocument();
  });

  // 一覧の取得が落ちたときはエラーを出す（**空の辞書と区別する**——
  // 区別しないと「タグが 1 つも無い」と誤読して重複登録を招く）。
  it('reports a failure to load the dictionary', async () => {
    mockApi({ tags: new ApiError('server', 'サーバでエラーが発生しました。', 500) });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('タグ辞書を読み込めませんでした。');
  });

  // 削除以外の失敗（重複名の 409 など）は**件数つきの文言にしない**——
  // `usageCount` を持たないので、専用文言を出すと嘘になる。
  it('falls back to the generic message when a conflict carries no usage count', async () => {
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [
            'タグ「規程」は既に辞書にあります。',
          ]),
        ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: 'タグ辞書' }));
    await user.type(screen.getByLabelText('タグ名（必須）'), '規程');
    await user.click(screen.getByRole('button', { name: '追加' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('タグ「規程」は既に辞書にあります。');
    expect(alert).not.toHaveTextContent('件の文書で使われている');
  });

  // ── FR-17, SC-09, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): 辺の型辞書 ──
  //
  // **タグ辞書と同じ操作体系を測る。** INDEX 決定 18 が「同じ規則をタグ辞書にも適用する」と
  // 定めており、**規則が同じなら試験も同型にする**（別々の形にすると、片方だけ壊れても気付けない）。

  it('lists edge types with their layer, direction and usage counts', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));

    expect(await screen.findByText('cites')).toBeInTheDocument();

    // **表の中だけを見る。** 追加フォームの層セレクトが同じ語（中核 / 推奨追加）を
    // `<option>` として持つので、画面全体で引くと追加フォームの選択肢と衝突する。
    const table = within(screen.getByRole('table', { name: '辺の型辞書の一覧' }));
    expect(table.getByText('342')).toBeInTheDocument();
    // 層は表示名へ写す（生値の core をそのまま出さない）。
    expect(table.getByText('中核')).toBeInTheDocument();
    expect(table.getByText('推奨追加')).toBeInTheDocument();
    // ★ 方向は**語で書く**（記号や色だけで対称／非対称を示さない。INDEX 決定 21）。
    expect(table.getAllByText('非対称')).toHaveLength(2);
  });

  // 🔴 **本区画の主眼。** 参照が 1 件でもあれば削除を拒否し、**件数を示す**。
  it('shows the usage count when an edge-type deletion is refused', async () => {
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError(
            'conflict',
            '競合が発生しました。',
            409,
            ['型「cites」は 342 本の辺で使われているため削除できません。'],
            { error: 'edge_type_in_use', usageCount: 342 },
          ),
        ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.click(await screen.findByRole('button', { name: '辺の型を削除: cites' }));

    // 件数が**翻訳済みの文の中に**出る（サーバの日本語をそのまま流していない）。
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'この辺の型は 342 本の辺で使われているため削除できません。',
    );
    // 409 は障害ではなく拒否である。ラベルも「注意」にする（INDEX 決定 21 の敷衍）。
    expect(screen.getByRole('alert')).toHaveTextContent('注意');
  });

  it('deletes an unused edge type', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.click(await screen.findByRole('button', { name: '辺の型を削除: part-of' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(([, init]) => (init as RequestInit)?.method === 'DELETE'),
      ).toBe(true),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('adds an edge type to the dictionary from the screen', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.type(screen.getByLabelText('型名（必須）'), 'refines');
    await user.click(screen.getByRole('button', { name: '追加' }));

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.some(
          ([path, init]) => path === '/edge-types' && (init as RequestInit)?.method === 'POST',
        ),
      ).toBe(true),
    );
    // 層と対称性も送る（後段は 3 つとも必須である）。
    expect(sentBody(mocks.apiRequest.mock.calls)).toEqual({
      name: 'refines',
      layer: 'recommended',
      isSymmetric: false,
    });
  });

  // ADR-0033 決定 9: **改名しても辺は 1 本も書き換わらない**（辺は型 ID を参照している）。
  // したがって画面は「何件へ反映しています」と言わない —— **起きていない処理を待っているように
  // 見える**（タグ辞書は非同期の射影があるので件数を出す。**非対称は意図である**）。
  it('renames an edge type and says the existing edges follow automatically', async () => {
    const user = userEvent.setup();
    mockApi({
      write: () =>
        Promise.resolve(
          jsonResponse({
            id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            name: 'cites-to',
            layer: 'core',
            isSymmetric: false,
            isSeed: true,
            usageCount: 342,
          }),
        ),
    });
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.click(await screen.findByRole('button', { name: '辺の型を改名: cites' }));
    await user.type(screen.getByLabelText('辺の型の新しい名前: cites'), '-to');
    await user.click(screen.getByRole('button', { name: '保存' }));

    const status = await screen.findByRole('status');
    expect(status).toHaveTextContent('辺の型を改名しました。');
    expect(status).toHaveTextContent('既存の辺はそのまま新しい名前で表示されます。');
    // 「N 件へ反映しています」は出さない（波及が無いため）。
    expect(status).not.toHaveTextContent('件の');
  });

  // ADR-0033 決定 3: 未定義の型は `related` へ丸め、**警告として記録する**（拒否も破棄もしない）。
  // 丸めるのは後段だが、**管理者は「型を消すと以後の抽出が related に寄る」ことを知って判断する**
  // 必要がある。画面がその帰結を告げないと、削除は「ただ消える」操作に見える。
  it('tells the administrator that unknown types fall back to related', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));

    expect(await screen.findByText(/related へ丸められ/)).toBeInTheDocument();
    expect(screen.getByText(/警告として記録されます/)).toBeInTheDocument();
  });

  it('says so when the edge-type dictionary is empty', async () => {
    const user = userEvent.setup();
    mockApi({ edgeTypes: [] });
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));

    expect(await screen.findByText('辺の型は登録されていません。')).toBeInTheDocument();
  });

  it('reports a failure to load the edge-type dictionary', async () => {
    const user = userEvent.setup();
    mockApi({ edgeTypes: new ApiError('server', 'サーバーエラー', 500) });
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      '辺の型辞書を読み込めませんでした。',
    );
  });

  // 名前の重複（409）は使用件数を持たないので、**既定の文言へ落ちる**
  // （削除拒否の文言を流用すると「0 本で使われている」という無意味な文になる）。
  it('falls back to the generic message when an edge-type conflict carries no usage count', async () => {
    const user = userEvent.setup();
    mockApi({
      write: () =>
        Promise.reject(
          new ApiError('conflict', '競合が発生しました。', 409, [
            '型「cites」は既に辞書にあります。',
          ]),
        ),
    });
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.type(screen.getByLabelText('型名（必須）'), 'cites');
    await user.click(screen.getByRole('button', { name: '追加' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('型「cites」は既に辞書にあります。');
    expect(alert).not.toHaveTextContent('本の辺で使われている');
  });

  // 🔴 **取り違えの検査（#1241 の主題）。** 画面が引くのは `/edge-types`（使用件数つき）であって
  // `/graph/edge-types`（描画用カタログ・件数なし）ではない。**取り違えると
  // 一般利用者が 403 になるか、ABAC で絞られていない集計値が漏れる。**
  it('reads the admin dictionary, never the public drawing catalog', async () => {
    const user = userEvent.setup();
    mockApi();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await screen.findByText('cites');

    const paths = mocks.apiRequest.mock.calls.map(([path]) => path);
    expect(paths).toContain('/edge-types');
    expect(paths).not.toContain('/graph/edge-types');
  });

  // 直近の 1 件だけを出す（[[IADR-0127]] 決定 7）。**2 つのミューテーションを跨いで測る** ——
  // TanStack Query は同じミューテーションの再実行では状態を戻すので、跨がないと穴が見えない。
  it('drops a stale edge-type failure when a different operation succeeds', async () => {
    let mode: 'conflict' | 'ok' = 'conflict';
    mockApi({
      write: () =>
        mode === 'conflict'
          ? Promise.reject(
              new ApiError('conflict', '競合が発生しました。', 409, [], {
                error: 'edge_type_in_use',
                usageCount: 342,
              }),
            )
          : Promise.resolve(
              jsonResponse({
                id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
                name: 'cites-to',
                layer: 'core',
                isSymmetric: false,
                isSeed: true,
                usageCount: 342,
              }),
            ),
    });
    const user = userEvent.setup();
    await renderPage();

    await user.click(await screen.findByRole('tab', { name: '辺の型' }));
    await user.click(await screen.findByRole('button', { name: '辺の型を削除: cites' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('342 本の辺で使われている');

    mode = 'ok';
    await user.click(await screen.findByRole('button', { name: '辺の型を改名: cites' }));
    await user.click(screen.getByRole('button', { name: '保存' }));

    expect(await screen.findByRole('status')).toHaveTextContent('辺の型を改名しました。');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
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
      expect(screen.queryByRole('heading', { name: '管理者設定（ABAC）' })).not.toBeInTheDocument();
      // 権限外では ABAC 管理 API を呼ばない（要求の有無から存在を推測させない）。
      expect(mocks.apiRequest).not.toHaveBeenCalled();
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
