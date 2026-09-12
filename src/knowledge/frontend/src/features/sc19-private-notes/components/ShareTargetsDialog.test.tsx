import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-19 主要素 3, UC-11, FR-19, ADR-0098 決定 1 / IADR-0445（#1445）/ IADR-0447（#1447）:
// 公開範囲の変更ダイアログ。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（`PrivateNotesPage.test.tsx` と同じ作法。MSW は使わない）。
//
// 🔴 **否定形は陽性対照と対で置く。** 本ファイルが固定する否定形は 2 つある ——
//   ① **利用者名（`subjectId`）が DOM に現れない**（対: 表示名は現れる）
//   ② **グループ識別子（Keycloak のグループ ID）が DOM に現れない**（対: 表示名とパスは現れる）
// どちらも「何も描かない実装」でも片側だけなら緑になる。
//
// ［2026-09-12 / #1447］**「グループの導線が無い」の陰性は反転した。** ADR-0098 決定 2 の
// 暫定手段（配線が入るまで描かない）が解除され、**グループ指定が実際に効く**ようになったため、
// 本ファイルは「導線が在る」ことと「告知が撤去された」ことを固定する。
//
// 🔴 **時刻を進められるようにする。** 検索入力は 300ms のデバウンスを通るため、
// 偽のタイマーでは候補が出ない。`shouldAdvanceTime` 付きの偽タイマーにして
// `userEvent` の `advanceTimers` を噛み合わせる（`PrivateNotesPage.test.tsx` と同じ設定）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc19PrivateNotesRoute } from '../routes/sc19PrivateNotesRoute';

const GB = 1024 ** 3;
const NOTE_ID = '00000000-0000-0000-0000-000000000001';

/** 個人へ 2 件共有されている資料（ダイアログを開く行）。 */
const NOTE = {
  id: NOTE_ID,
  title: '設計メモ',
  vaultPath: '設計メモ.md',
  version: 3,
  bytes: 100 * 1024,
  contentHash: 'abc',
  includeInSearch: false,
  includeInGraph: false,
  includeInAi: false,
  deleted: false,
  deletedAt: null,
  purgeAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-09-10T09:30:00Z',
  visibility: 'users',
  sharedUserCount: 2,
  sharedGroupCount: 0,
  syncState: 'target',
  tags: [],
};

/** グループ共有も持つ資料。 */
const NOTE_WITH_GROUPS = {
  ...NOTE,
  sharedGroupCount: 2,
};

/**
 * グループの識別子（Keycloak のグループ ID）。
 *
 * 🔴 **`g-` 接頭辞と UUID の両方の形を含める。** 陰性対照は「識別子が DOM に出ない」ことであり、
 * 実装が一部だけ（たとえばパスだけ）を隠しても緑にならないようにする。
 */
const SALES_GROUP_ID = 'g-1f3c2b4a-5d6e-47f8-9a0b-1c2d3e4f5a6b';
const DEV_GROUP_ID = 'g-2a4d3c5b-6e7f-48a9-b0c1-2d3e4f5a6b7c';
/** Keycloak 側で消されたグループ（`resolve` が返さない）。**台帳の行は残る。** */
const GONE_GROUP_ID = 'g-3b5e4d6c-7f80-49ba-c1d2-3e4f5a6b7c8d';

const RESOLVED_GROUPS = [{ id: SALES_GROUP_ID, displayName: '営業部', path: '/teams/sales' }];

/** グループの検索候補。**既に共有済み（営業部）も返す**（画面が除く）。 */
const GROUP_CANDIDATES = [
  { id: SALES_GROUP_ID, displayName: '営業部', path: '/teams/sales' },
  { id: DEV_GROUP_ID, displayName: '開発部', path: '/teams/dev' },
];

/**
 * 台帳の 3 行。
 *
 * `hanako` は `resolve` が返す（有効）、`taro` は無効化済み、`ghost` は**引けない**
 * （利用者ごと消えている）。**3 つとも行は出る**（消すと取り消せなくなる）。
 */
const SHARES = [
  {
    subjectType: 'user',
    subjectId: 'hanako',
    grantedBy: 'tester',
    createdAt: '2026-09-02T00:00:00Z',
  },
  {
    subjectType: 'user',
    subjectId: 'taro',
    grantedBy: 'tester',
    createdAt: '2026-09-03T00:00:00Z',
  },
  {
    subjectType: 'user',
    subjectId: 'ghost',
    grantedBy: 'tester',
    createdAt: '2026-09-04T00:00:00Z',
  },
];

/** グループ共有が混ざった台帳（引ける 1 件 ＋ **引けない 1 件**）。 */
const SHARES_WITH_GROUP = [
  ...SHARES,
  {
    subjectType: 'group',
    subjectId: SALES_GROUP_ID,
    grantedBy: 'tester',
    createdAt: '2026-09-05T00:00:00Z',
  },
  {
    subjectType: 'group',
    subjectId: GONE_GROUP_ID,
    grantedBy: 'tester',
    createdAt: '2026-09-06T00:00:00Z',
  },
];

const RESOLVED = [
  { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
  { username: 'taro', displayName: '太郎 タロウ', enabled: false },
];

/** 検索の候補。**自分自身（`tester`）と共有済み（`hanako`）を含めて返す**（画面が除く）。 */
const CANDIDATES = [
  { username: 'hanako', displayName: '花子 ハナコ', enabled: true },
  { username: 'jiro', displayName: '次郎 ジロウ', enabled: true },
  { username: 'tester', displayName: 'tester', enabled: true },
];

function respond({
  notes = [NOTE] as unknown[],
  shares = SHARES as unknown[],
  resolved = RESOLVED as unknown[],
  candidates = CANDIDATES as unknown[],
  resolvedGroups = RESOLVED_GROUPS as unknown[],
  groupCandidates = GROUP_CANDIDATES as unknown[],
  grantStatus = 201,
}: {
  notes?: unknown[];
  shares?: unknown[];
  resolved?: unknown[];
  candidates?: unknown[];
  resolvedGroups?: unknown[];
  groupCandidates?: unknown[];
  grantStatus?: number;
} = {}) {
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す）。
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    const method = init?.method ?? 'GET';
    if (path === '/private-notes' && method === 'GET') {
      return Promise.resolve(
        jsonResponse({ usage: { usedBytes: 0.2 * GB, limitBytes: GB, percent: 20 }, notes }),
      );
    }
    if (path === `/private-notes/${NOTE_ID}/shares` && method === 'GET') {
      return Promise.resolve(jsonResponse(shares));
    }
    if (path === `/private-notes/${NOTE_ID}/shares` && method === 'POST') {
      if (grantStatus !== 201) return Promise.reject(new Error('boom'));
      return Promise.resolve(jsonResponse({}, 201));
    }
    if (path.startsWith(`/private-notes/${NOTE_ID}/shares/`) && method === 'DELETE') {
      return Promise.resolve(jsonResponse({}, 204));
    }
    if (path === '/users/resolve') return Promise.resolve(jsonResponse(resolved));
    if (path.startsWith('/users/lookup')) return Promise.resolve(jsonResponse(candidates));
    if (path === '/groups/resolve') return Promise.resolve(jsonResponse(resolvedGroups));
    if (path.startsWith('/groups/lookup')) return Promise.resolve(jsonResponse(groupCandidates));
    return Promise.resolve(jsonResponse({}));
  });
}

/**
 * 共有台帳への書き込み（付与・取り消し）。
 *
 * 🔴 **`POST /users/resolve`・`POST /groups/resolve` は除く。** 契約上は POST だが**照会**であり
 * （表示名を引くだけ）、台帳を 1 バイトも変えない。混ぜると「閉じただけで書き込みが 1 件出た」
 * ように読める。
 */
function writes(): { path: string; method: string; body: unknown }[] {
  return mocks.apiRequest.mock.calls
    .map((c) => ({
      path: c[0] as string,
      method: (c[1] as { method?: string } | undefined)?.method ?? 'GET',
      body: (c[1] as { body?: string } | undefined)?.body,
    }))
    .filter(
      (c) => c.method !== 'GET' && c.path !== '/users/resolve' && c.path !== '/groups/resolve',
    )
    .map((c) => ({ ...c, body: typeof c.body === 'string' ? JSON.parse(c.body) : c.body }));
}

/**
 * デバウンス（300ms）を越えて時計を進める。
 *
 * 🔴 **`act` で包む。** タイマーが切れると `useDebounced` が state を更新するため、
 * 包まないと React が「act の外での更新」を警告し、**テストの出力が読めなくなる**
 * （警告は落とさないので、放置すると次の本当の警告が埋もれる）。
 */
async function settleDebounce() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(400);
  });
}

/**
 * 行操作からダイアログを開き、その中を引く器（`within`）を返す。
 *
 * 🔴 **`within` は各ケースの中で呼ぶ。** 補助関数の戻り値として渡すと
 * `testing-library/prefer-screen-queries` が「render の結果を分配した」と読んで落ちる
 * （既存の `PrivateNotesPage.test.tsx` が `const table = within(...)` と書いているのと同じ形に揃える）。
 */
async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await renderUnitRoute((shell) => [createSc19PrivateNotesRoute(shell)], {
    initialEntry: '/my/notes',
  });
  await screen.findByText('設計メモ');
  await user.click(screen.getByRole('button', { name: '共有先を変更する' }));
  await screen.findByRole('dialog');
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date('2026-09-12T00:00:00Z'));
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('SC-19 共有先ダイアログ: 現在の指定先（#1445）', () => {
  it('個人の共有先を表示名で並べ、各行に取り消しを置く', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    expect(await dialog.findByText('花子 ハナコ')).toBeInTheDocument();
    // 無効化済みは表示名に併記する（退職者への共有が残っていることに気付けるようにする）。
    expect(dialog.getByText('太郎 タロウ（無効化済み）')).toBeInTheDocument();
    // 引けなかった名前も**行を消さない**（消すと取り消せなくなる）。
    expect(dialog.getByText('（不明な利用者）')).toBeInTheDocument();
    // 3 行それぞれに取り消しが付く。
    expect(dialog.getAllByRole('button', { name: '取り消す' })).toHaveLength(3);
  });

  it('🔴 利用者名（識別子）を DOM に出さない（陽性対照: 表示名は在る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 表示名は在る（何も描かない実装と区別する）。
    expect(await dialog.findByText('花子 ハナコ')).toBeInTheDocument();

    // 🔴 陰性: ADR-0098 決定 1 —— 識別子は本文にも属性値（title 等）にも出さない。
    const html = screen.getByRole('dialog').outerHTML;
    for (const username of ['hanako', 'taro', 'ghost']) {
      expect(html, username).not.toContain(username);
    }
  });

  it('グループ共有を表示名とパスで並べ、引けないグループも行を残す（#1447）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ notes: [NOTE_WITH_GROUPS], shares: SHARES_WITH_GROUP });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 個人の行（3 行）も出る（種別ごとの見出しで分かれる）。
    expect(await dialog.findByText('花子 ハナコ')).toBeInTheDocument();
    expect(dialog.getByRole('heading', { name: '個人' })).toBeInTheDocument();
    expect(dialog.getByRole('heading', { name: 'グループ' })).toBeInTheDocument();

    // グループは表示名を主、パスを副に出す（同名のグループを区別する手掛かり）。
    expect(dialog.getByText('営業部')).toBeInTheDocument();
    expect(dialog.getByText('/teams/sales')).toBeInTheDocument();
    // 🔴 `resolve` が返さない（Keycloak 側で消された）グループも**行を消さない** ——
    // 消すと取り消せなくなり、「見えない共有」が恒久化する。
    expect(dialog.getByText('（見つからないグループ）')).toBeInTheDocument();

    // 個人 3 行 ＋ グループ 2 行のすべてに取り消しが付く。
    expect(dialog.getAllByRole('button', { name: '取り消す' })).toHaveLength(5);

    // ［2026-09-12 / #1447］暫定手段の解除: **「本画面では変更できません」の告知は撤去した。**
    expect(dialog.queryByText(/本画面では変更できません/)).not.toBeInTheDocument();
    expect(dialog.queryByText(/グループへの共有が/)).not.toBeInTheDocument();
  });

  it('🔴 グループ識別子を DOM に出さない（陽性対照: 表示名とパスは在る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ notes: [NOTE_WITH_GROUPS], shares: SHARES_WITH_GROUP });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 表示名とパスは在る（何も描かない実装と区別する）。
    expect(await dialog.findByText('営業部')).toBeInTheDocument();
    expect(dialog.getByText('/teams/sales')).toBeInTheDocument();

    // 🔴 陰性: ADR-0098 決定 1 —— 識別子は本文にも属性値（title 等）にも出さない。
    const html = screen.getByRole('dialog').outerHTML;
    for (const id of [SALES_GROUP_ID, GONE_GROUP_ID]) {
      expect(html, id).not.toContain(id);
    }
  });

  it('共有が 1 件も無いときは空状態を出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ shares: [], resolved: [] });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    expect(await dialog.findByText(/共有している相手はいません/)).toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: '取り消す' })).not.toBeInTheDocument();
  });

  it('取り消しは subjectType=user のパスへ DELETE を送る', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.click(dialog.getAllByRole('button', { name: '取り消す' })[0]);

    await waitFor(() => expect(writes().length).toBeGreaterThan(0));
    expect(writes()[0]).toMatchObject({
      method: 'DELETE',
      path: `/private-notes/${NOTE_ID}/shares/user/hanako`,
    });
  });
});

describe('SC-19 共有先ダイアログ: 利用者の追加（#1445）', () => {
  it('2 文字未満では検索せず、理由を書く', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    expect(dialog.getByText(/2 文字以上入力すると候補が出ます/)).toBeInTheDocument();

    await user.type(dialog.getByLabelText('名前で検索する'), 'じ');
    await settleDebounce();
    expect(
      mocks.apiRequest.mock.calls.filter((c) => String(c[0]).startsWith('/users/lookup')),
    ).toHaveLength(0);
    expect(dialog.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('検索 → 候補を選ぶ → 追加で、subjectType=user の本文を送る', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.type(dialog.getByLabelText('名前で検索する'), '次郎');
    await settleDebounce();

    const options = await dialog.findAllByRole('option');
    // ★ 候補は**表示名だけ**を出す。自分自身（tester）と共有済み（hanako）は落ちるので 1 件。
    expect(options).toHaveLength(1);
    expect(options[0]).toHaveTextContent('次郎 ジロウ');

    // 選ぶ前は追加を押せない（「何を追加するのか」が決まっていない）。
    expect(dialog.getByRole('button', { name: '追加' })).toBeDisabled();
    await user.click(options[0]);
    expect(dialog.getByRole('button', { name: '追加' })).toBeEnabled();
    await user.click(dialog.getByRole('button', { name: '追加' }));

    await waitFor(() => expect(writes().length).toBeGreaterThan(0));
    expect(writes()[0]).toMatchObject({
      method: 'POST',
      path: `/private-notes/${NOTE_ID}/shares`,
      body: { subjectType: 'user', subjectId: 'jiro' },
    });
  });

  it('候補が自分自身と共有済みだけなら「該当する利用者がいません」になる', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ candidates: [CANDIDATES[0], CANDIDATES[2]] });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.type(dialog.getByLabelText('名前で検索する'), 'はな');
    await settleDebounce();

    expect(await dialog.findByText(/該当する利用者がいません/)).toBeInTheDocument();
    expect(dialog.queryByRole('option')).not.toBeInTheDocument();
  });

  it('追加に失敗したら理由を出し、ダイアログを閉じない', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ grantStatus: 409 });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.type(dialog.getByLabelText('名前で検索する'), '次郎');
    await settleDebounce();
    await user.click((await dialog.findAllByRole('option'))[0]);
    await user.click(dialog.getByRole('button', { name: '追加' }));

    expect(await dialog.findByRole('alert')).toHaveTextContent(/共有先を変更できませんでした/);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});

describe('SC-19 共有先ダイアログ: グループ指定（#1447）', () => {
  it('🔴 指定先の種別を選べる（陽性対照: 個人の追加も在る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 個人を追加する面は在る（検索入力・追加ボタン・取り消し）。
    expect(dialog.getByLabelText('名前で検索する')).toBeInTheDocument();
    expect(dialog.getByRole('button', { name: '追加' })).toBeInTheDocument();
    expect((await dialog.findAllByRole('button', { name: '取り消す' })).length).toBeGreaterThan(0);
    expect(dialog.getByRole('heading', { name: '指定先を追加' })).toBeInTheDocument();

    // 🔴 種別の切替は**選択の部品**である（既定は個人）。色だけに頼らず、選択状態は役割が持つ。
    expect(dialog.getByRole('radio', { name: '個人' })).toBeChecked();
    expect(dialog.getByRole('radio', { name: 'グループ' })).not.toBeChecked();

    // グループへ切り替えると検索の対象が変わる（ラベルそのものが変わる）。
    await user.click(dialog.getByRole('radio', { name: 'グループ' }));
    expect(dialog.getByRole('radio', { name: 'グループ' })).toBeChecked();
    expect(dialog.getByLabelText('グループ名で検索する')).toBeInTheDocument();
    expect(dialog.queryByLabelText('名前で検索する')).not.toBeInTheDocument();
  });

  it('グループを検索して追加すると subjectType=group の本文を送る（共有済みは候補から除く）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ notes: [NOTE_WITH_GROUPS], shares: SHARES_WITH_GROUP });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('営業部');
    await user.click(dialog.getByRole('radio', { name: 'グループ' }));
    // 🔴 2 文字以上でなければ問い合わせない（契約 `q` の下限。個人の検索と同じ）。
    await user.type(dialog.getByLabelText('グループ名で検索する'), '開発');
    await settleDebounce();

    // 既に共有済みの営業部は落ちるので 1 件（開発部）。
    const options = await dialog.findAllByRole('option');
    expect(options).toHaveLength(1);
    // 候補も表示名を主・パスを副に出し、**識別子は出さない。**
    expect(options[0]).toHaveTextContent('開発部');
    expect(options[0]).toHaveTextContent('/teams/dev');
    expect(options[0].outerHTML).not.toContain(DEV_GROUP_ID);

    await user.click(options[0]);
    await user.click(dialog.getByRole('button', { name: '追加' }));

    await waitFor(() => expect(writes().length).toBeGreaterThan(0));
    expect(writes()[0]).toMatchObject({
      method: 'POST',
      path: `/private-notes/${NOTE_ID}/shares`,
      body: { subjectType: 'group', subjectId: DEV_GROUP_ID },
    });
  });

  it('グループの取り消しは subjectType=group のパスへ DELETE を送る', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ notes: [NOTE_WITH_GROUPS], shares: SHARES_WITH_GROUP });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('営業部');
    const rows = dialog.getAllByRole('listitem');
    const salesRow = rows.find((row) => row.textContent?.includes('営業部'));
    expect(salesRow).toBeDefined();
    await user.click(within(salesRow as HTMLElement).getByRole('button', { name: '取り消す' }));

    await waitFor(() => expect(writes().length).toBeGreaterThan(0));
    expect(writes()[0]).toMatchObject({
      method: 'DELETE',
      path: `/private-notes/${NOTE_ID}/shares/group/${SALES_GROUP_ID}`,
    });
  });

  it('種別を切り替えると検索語と選択を捨てる（別の名前空間の値を持ち越さない）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.type(dialog.getByLabelText('名前で検索する'), '次郎');
    await settleDebounce();
    await user.click((await dialog.findAllByRole('option'))[0]);
    expect(dialog.getByRole('button', { name: '追加' })).toBeEnabled();

    await user.click(dialog.getByRole('radio', { name: 'グループ' }));

    // 🔴 選んだ利用者名をグループの追加へ持ち越さない（押せる状態も残さない）。
    expect(dialog.getByRole('button', { name: '追加' })).toBeDisabled();
    expect(dialog.getByLabelText('グループ名で検索する')).toHaveValue('');
    expect(dialog.getByText(/2 文字以上入力すると候補が出ます/)).toBeInTheDocument();
    // 切り替えただけではグループを引かない（検索語が空なので問い合わせない）。
    await settleDebounce();
    expect(
      mocks.apiRequest.mock.calls.filter((c) => String(c[0]).startsWith('/groups/lookup')),
    ).toHaveLength(0);
  });
});

describe('SC-19 共有先ダイアログ: 降りる（#1445）', () => {
  it('閉じるとダイアログが消え、共有の要求は 1 件も出ない', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.click(dialog.getByRole('button', { name: '閉じる' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(writes()).toHaveLength(0);
  });

  it('Esc でも閉じられる（キーボードだけで降りられる）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    await dialog.findByText('花子 ハナコ');
    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(writes()).toHaveLength(0);
  });
});
