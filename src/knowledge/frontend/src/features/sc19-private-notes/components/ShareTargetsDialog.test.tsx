import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-19 主要素 3, UC-11, FR-19, ADR-0098 / IADR-0445（#1445）: 公開範囲の変更ダイアログ。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（`PrivateNotesPage.test.tsx` と同じ作法。MSW は使わない）。
//
// 🔴 **否定形は陽性対照と対で置く。** 本ファイルが固定する否定形は 2 つある ——
//   ① **利用者名（`subjectId`）が DOM に現れない**（対: 表示名は現れる）
//   ② **「グループ」の文言・導線が無い**（対: 個人の追加・取り消しは在る）
// どちらも「何も描かない実装」でも片側だけなら緑になる。
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

/** グループ共有も持つ資料（台帳を消さず `Note` で告げる経路）。 */
const NOTE_WITH_GROUPS = {
  ...NOTE,
  sharedGroupCount: 3,
};

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

/** グループ共有が混ざった台帳（**個人の行だけを出す**ことを見る）。 */
const SHARES_WITH_GROUP = [
  ...SHARES,
  {
    subjectType: 'group',
    subjectId: 'sales',
    grantedBy: 'tester',
    createdAt: '2026-09-05T00:00:00Z',
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
  grantStatus = 201,
}: {
  notes?: unknown[];
  shares?: unknown[];
  resolved?: unknown[];
  candidates?: unknown[];
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
    return Promise.resolve(jsonResponse({}));
  });
}

/**
 * 共有台帳への書き込み（付与・取り消し）。
 *
 * 🔴 **`POST /users/resolve` は除く。** 契約上は POST だが**照会**であり（表示名を引くだけ）、
 * 台帳を 1 バイトも変えない。混ぜると「閉じただけで書き込みが 1 件出た」ように読める。
 */
function writes(): { path: string; method: string; body: unknown }[] {
  return mocks.apiRequest.mock.calls
    .map((c) => ({
      path: c[0] as string,
      method: (c[1] as { method?: string } | undefined)?.method ?? 'GET',
      body: (c[1] as { body?: string } | undefined)?.body,
    }))
    .filter((c) => c.method !== 'GET' && c.path !== '/users/resolve')
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

  it('グループ共有は行に出さず、残っていることだけを告げる（台帳は消さない）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ notes: [NOTE_WITH_GROUPS], shares: SHARES_WITH_GROUP });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 個人の行は出る（3 行）。
    expect(await dialog.findByText('花子 ハナコ')).toBeInTheDocument();
    expect(dialog.getAllByRole('button', { name: '取り消す' })).toHaveLength(3);
    // グループの行は出ない（`sales` は識別子でもあるので DOM 全体で見る）。
    expect(screen.getByRole('dialog').outerHTML).not.toContain('sales');
    // 残っていることは告げる（黙って消えたと読ませない）。
    expect(dialog.getByText(/グループへの共有が 3 件あります/)).toBeInTheDocument();
  });

  it('個人への共有が無いときは空状態を出す', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond({ shares: [], resolved: [] });
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    expect(await dialog.findByText(/個人への共有はありません/)).toBeInTheDocument();
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

describe('SC-19 共有先ダイアログ: グループの導線を置かない（ADR-0098 決定 2）', () => {
  it('🔴 「グループ」を選ぶ導線・文言が無い（陽性対照: 個人の追加は在る）', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    respond();
    await openDialog(user);
    const dialog = within(screen.getByRole('dialog'));

    // ★ 陽性対照: 個人を追加する面は在る（検索入力・追加ボタン・取り消し）。
    expect(dialog.getByLabelText('名前で検索する')).toBeInTheDocument();
    expect(dialog.getByRole('button', { name: '追加' })).toBeInTheDocument();
    expect((await dialog.findAllByRole('button', { name: '取り消す' })).length).toBeGreaterThan(0);
    expect(dialog.getByRole('heading', { name: '利用者を追加' })).toBeInTheDocument();

    // 🔴 陰性: グループを選ぶ導線が無い。**種別を選ばせる部品そのものを置かない。**
    expect(dialog.queryByRole('combobox')).not.toBeInTheDocument();
    expect(dialog.queryByRole('radio')).not.toBeInTheDocument();
    expect(dialog.queryByRole('button', { name: /グループ/ })).not.toBeInTheDocument();
    expect(dialog.queryByLabelText(/グループ/)).not.toBeInTheDocument();
    // 🔴 陰性: 「グループ」という語が 1 つも現れない（グループ共有が 0 件の資料である）。
    expect(screen.getByRole('dialog').outerHTML).not.toContain('グループ');
  });

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
