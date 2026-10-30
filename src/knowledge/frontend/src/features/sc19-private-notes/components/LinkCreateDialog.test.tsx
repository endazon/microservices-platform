import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';
import { ApiError } from '@foundation/api/ApiError';

// FR-17, SC-19, ADR-0034 決定 2・8, [[IADR-0655]] (#2343): リンク作成 UI（関連文書の明示的な付与）。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは apiRequest に当てる。
// 非 2xx は apiRequest が `ApiError` を投げる形なので、失敗は `ApiError` で拒否して模す。
//
// 🔴 **404 は「見つかりません。」だけを出し、「権限」の語を出さない**（存在秘匿。陽性対照は 201 の成功の文言）。
// 🔴 **候補は後段が絞ったものだけを出す**（画面は自分自身＝起点の資料を除くだけ）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc19PrivateNotesRoute } from '../routes/sc19PrivateNotesRoute';

const GB = 1024 ** 3;
const NOTE_ID = '00000000-0000-0000-0000-000000000001';
const OFF_NOTE_ID = '00000000-0000-0000-0000-000000000002';
const TARGET_ID = 'aaaaaaaa-0000-0000-0000-000000000001';
const MY_OTHER_NOTE_ID = 'aaaaaaaa-0000-0000-0000-000000000002';
const TYPE_ID = 'eeeeeeee-0000-0000-0000-000000000001';

const baseNote = {
  vaultPath: 'x.md',
  version: 1,
  bytes: 1024,
  contentHash: 'abc',
  includeInSearch: false,
  includeInAi: false,
  deleted: false,
  deletedAt: null,
  purgeAt: null,
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-10T00:00:00Z',
  visibility: 'private',
  sharedUserCount: 0,
  sharedGroupCount: 0,
  syncState: 'target',
  tags: [],
};

/** グラフに表示する（ノードがある）資料と、表示しない（ノードが無い）資料。 */
const NOTES = [
  { ...baseNote, id: NOTE_ID, title: '設計メモ', includeInGraph: true },
  { ...baseNote, id: OFF_NOTE_ID, title: '下書きメモ', includeInGraph: false },
];

/** 候補。**起点の資料そのもの**も返す（画面が除く）。 */
const CANDIDATES = [
  { documentId: TARGET_ID, title: '設計方針（組織）', isPrivateNote: false },
  { documentId: MY_OTHER_NOTE_ID, title: '設計の補足メモ', isPrivateNote: true },
  { documentId: NOTE_ID, title: '設計メモ', isPrivateNote: true },
];

const EDGE_TYPES = [
  { id: TYPE_ID, name: 'references', layer: 'core', isSymmetric: false },
  { id: 'eeeeeeee-0000-0000-0000-000000000002', name: 'related', layer: 'core', isSymmetric: true },
];

function respond(createResult: () => Promise<unknown>) {
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    const method = init?.method ?? 'GET';
    if (path === '/private-notes' && method === 'GET') {
      return Promise.resolve(
        jsonResponse({ usage: { usedBytes: 0.1 * GB, limitBytes: GB, percent: 10 }, notes: NOTES }),
      );
    }
    if (path.startsWith('/graph/link-candidates')) return Promise.resolve(jsonResponse(CANDIDATES));
    if (path === '/graph/edge-types') return Promise.resolve(jsonResponse(EDGE_TYPES));
    if (path === '/graph/edges' && method === 'POST') return createResult();
    return Promise.resolve(jsonResponse({}));
  });
}

const created = () =>
  Promise.resolve(
    jsonResponse(
      {
        id: 'ffffffff-0000-0000-0000-000000000001',
        sourceDocumentId: NOTE_ID,
        targetDocumentId: TARGET_ID,
        edgeTypeId: TYPE_ID,
        provenance: 'user',
      },
      201,
    ),
  );

async function settleDebounce() {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(400);
  });
}

/** 「設計メモ」の行からダイアログを開き、型を選び、候補を探して 1 件選ぶ。 */
async function chooseTarget(user: ReturnType<typeof userEvent.setup>) {
  await renderUnitRoute((shell) => [createSc19PrivateNotesRoute(shell)], {
    initialEntry: '/my/notes',
  });
  const row = (await screen.findByText('設計メモ')).closest('tr')!;
  await user.click(within(row).getByRole('button', { name: 'リンクを張る' }));
  const dialog = await screen.findByRole('dialog');
  await user.selectOptions(await within(dialog).findByLabelText('リンクの種類'), TYPE_ID);
  await user.type(within(dialog).getByLabelText('リンク先の文書を表題で探す'), '設計');
  await settleDebounce();
  await user.click(await within(dialog).findByRole('radio', { name: /設計方針（組織）/ }));
  return dialog;
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(new Date('2026-10-30T00:00:00Z'));
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('LinkCreateDialog (SC-19 リンク作成 UI)', () => {
  // 陽性対照: 候補を選んで作成すると、起点・終点・型が後段へ届き、成功の文言が出る。
  it('作成すると起点・終点・型を送り、成功を示す', async () => {
    respond(created);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const dialog = await chooseTarget(user);

    await user.click(within(dialog).getByRole('button', { name: 'リンクを作成' }));

    expect(await within(dialog).findByText('リンクを作成しました。')).toBeInTheDocument();
    const post = mocks.apiRequest.mock.calls.find(
      (c) => c[0] === '/graph/edges' && (c[1] as { method?: string })?.method === 'POST',
    );
    expect(JSON.parse((post![1] as { body: string }).body)).toEqual({
      sourceDocumentId: NOTE_ID,
      targetDocumentId: TARGET_ID,
      edgeTypeId: TYPE_ID,
    });
  });

  // 🔴 ADR-0034 決定 8: 後段の 404 は「見つかりません。」。「権限」の語を出さない。
  it('後段の 404 は「見つかりません。」とだけ示し、権限の語を出さない', async () => {
    respond(() => Promise.reject(new ApiError('notFound', 'not found', 404)));
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const dialog = await chooseTarget(user);

    await user.click(within(dialog).getByRole('button', { name: 'リンクを作成' }));

    expect(await within(dialog).findByText('見つかりません。')).toBeInTheDocument();
    expect(dialog.textContent).not.toMatch(/権限/);
  });

  // 409 edge_exists と 400 unknown_edge_type は、利用者が直せる理由として文言で言い分ける。
  it('409 edge_exists と 400 unknown_edge_type を文言で言い分ける', async () => {
    respond(() =>
      Promise.reject(new ApiError('conflict', 'conflict', 409, [], { error: 'edge_exists' })),
    );
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const dialog = await chooseTarget(user);
    await user.click(within(dialog).getByRole('button', { name: 'リンクを作成' }));
    expect(await within(dialog).findByText('このリンクはすでにあります。')).toBeInTheDocument();

    respond(() =>
      Promise.reject(new ApiError('validation', 'bad', 400, [], { error: 'unknown_edge_type' })),
    );
    await user.click(within(dialog).getByRole('button', { name: 'リンクを作成' }));
    expect(
      await within(dialog).findByText('選んだリンクの種類は使えません。種類を選び直してください。'),
    ).toBeInTheDocument();
    expect(within(dialog).queryByText('このリンクはすでにあります。')).not.toBeInTheDocument();
  });

  // 候補は後段の応答をそのまま出す。画面は起点の資料（自分自身）だけを除く。自分の個人資料は印を付けて出す。
  it('候補から起点の資料を除き、自分の個人資料には印を付ける', async () => {
    respond(created);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const dialog = await chooseTarget(user);

    const options = within(dialog).getAllByRole('radio');
    expect(options).toHaveLength(2);
    expect(
      within(dialog).getByRole('radio', { name: /設計の補足メモ.*自分の個人資料/ }),
    ).toBeInTheDocument();
    expect(within(dialog).queryByRole('radio', { name: /^設計メモ/ })).not.toBeInTheDocument();
    // 件数は出さない（後段も返さない）。
    expect(dialog.textContent).not.toMatch(/\d+\s*件/);
  });

  // グラフの露出が OFF の資料はノードが無く、必ず 404 になる。押させず、理由を示す。
  it('グラフに表示しない資料ではリンクを張れず、理由を示す', async () => {
    respond(created);
    await renderUnitRoute((shell) => [createSc19PrivateNotesRoute(shell)], {
      initialEntry: '/my/notes',
    });
    const offRow = (await screen.findByText('下書きメモ')).closest('tr')!;
    const onRow = screen.getByText('設計メモ').closest('tr')!;

    expect(within(offRow).getByRole('button', { name: 'リンクを張る' })).toBeDisabled();
    expect(
      within(offRow).getByText('「ナレッジグラフに表示する」をオンにするとリンクを張れます。'),
    ).toBeInTheDocument();
    // 陽性対照: グラフに表示する資料では押せる。
    expect(within(onRow).getByRole('button', { name: 'リンクを張る' })).toBeEnabled();
    await waitFor(() =>
      expect(mocks.apiRequest.mock.calls.some((c) => String(c[0]).startsWith('/graph/'))).toBe(
        false,
      ),
    );
  });
});
