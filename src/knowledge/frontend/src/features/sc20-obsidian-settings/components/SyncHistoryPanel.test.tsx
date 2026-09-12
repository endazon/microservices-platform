import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-20 主要素 6, UC-11, FR-20, ADR-0099 / IADR-0446（#1446）: 同期履歴の区画。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（`ObsidianSettingsPage.test.tsx` と同じ作法。MSW は使わない）。
//
// 🔴 **否定形（タイトル・パスの列が無い）は陽性対照（端末の列が在る）と対で置く。**
// 何も描かない実装でも否定形だけなら緑になる。
//
// 🔴 **区画の絞り込みは `within` を各ケースで書く。** 補助関数に包むと
// `testing-library/prefer-screen-queries` が「render の結果を分配した」と読んで落ちる
// （既存の `PrivateNotesPage.test.tsx` が `const table = within(...)` と書いているのと同じ形に揃える）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc20ObsidianSettingsRoute } from '../routes/sc20ObsidianSettingsRoute';

/** 送信の成功（新規 1 件）。 */
const PUSH_SUCCESS = {
  id: '00000000-0000-0000-0000-0000000000e1',
  occurredAt: '2026-09-11T10:00:00Z',
  deviceName: 'MacBook Pro',
  direction: 'push',
  added: 1,
  updated: 0,
  deleted: 0,
  conflicted: 0,
  outcome: 'success',
  failureReason: null,
};

/** 送信の失敗（競合）。**上の「同期の競合」区画へ誘導する文言が出る。** */
const PUSH_CONFLICT = {
  ...PUSH_SUCCESS,
  id: '00000000-0000-0000-0000-0000000000e2',
  occurredAt: '2026-09-11T09:00:00Z',
  deviceName: '会議室の PC',
  added: 0,
  conflicted: 1,
  outcome: 'failure',
  failureReason: 'version_conflict',
};

/** 受信の成功。**内訳がすべて 0** の行（「—」を出す経路）。 */
const PULL_EMPTY = {
  ...PUSH_SUCCESS,
  id: '00000000-0000-0000-0000-0000000000e3',
  occurredAt: '2026-09-11T08:00:00Z',
  deviceName: 'iPad',
  direction: 'pull',
  added: 0,
};

const ALL_ENTRIES = [PUSH_SUCCESS, PUSH_CONFLICT, PULL_EMPTY];

/**
 * BFF の面へ応答を割り当てる。
 *
 * **同期履歴以外は静かにしておく**（端末 0 件・フォルダ未設定・競合 0 件）。本ファイルが見るのは
 * 履歴の区画だけであり、他の区画に行が並ぶと表の引き当てが 2 つの表に当たる。
 */
function respond({
  history = ALL_ENTRIES as unknown[],
  historyFails = false,
}: { history?: unknown[]; historyFails?: boolean } = {}) {
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path === '/private-notes/sync-history') {
      return historyFails
        ? Promise.reject(new Error('boom'))
        : Promise.resolve(jsonResponse(history));
    }
    if (path === '/private-notes/devices') return Promise.resolve(jsonResponse([]));
    if (path === '/private-notes/sync-settings') {
      return Promise.resolve(
        jsonResponse({ targetFolders: [], updatedAt: '2026-09-11T00:00:00Z' }),
      );
    }
    if (path === '/private-notes/conflicts') return Promise.resolve(jsonResponse([]));
    return Promise.resolve(jsonResponse({}));
  });
}

/** 画面を描く。**履歴の区画の絞り込み（`within`）は各ケースの中で書く**（冒頭の注記）。 */
async function renderPage() {
  await renderUnitRoute((shell) => [createSc20ObsidianSettingsRoute(shell)], {
    initialEntry: '/my/obsidian',
  });
}

/** 履歴の区画（`Panel` ＝ `aria-label` 付きの `<section>` ＝ region）の要素。 */
const historyPanel = () => screen.getByRole('region', { name: '同期履歴' });

/** `/private-notes/sync-history` を引いた回数。 */
const historyCalls = () =>
  mocks.apiRequest.mock.calls.filter((c) => c[0] === '/private-notes/sync-history').length;

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('SC-20 同期履歴: 行の描画（#1446）', () => {
  it('実行日時 / 端末 / 方向 / 内訳 / 結果 / 失敗理由 の 6 列を出す', async () => {
    respond();
    await renderPage();
    const history = within(historyPanel());

    await history.findByText('MacBook Pro');
    for (const name of ['実行日時', '端末', '方向', '内訳', '結果', '失敗理由']) {
      expect(history.getByRole('columnheader', { name })).toBeInTheDocument();
    }
  });

  it('🔴 タイトル・パスの列を持たない（陽性対照: 端末の列は在る）', async () => {
    respond();
    await renderPage();
    const history = within(historyPanel());

    await history.findByText('MacBook Pro');
    // ★ 陽性対照: 端末の列は在る（何も描かない実装と区別する）。
    expect(history.getByRole('columnheader', { name: '端末' })).toBeInTheDocument();
    // 🔴 陰性: ADR-0099 決定 5 —— 資料のタイトルも Vault のパスも列に無い。
    expect(history.queryByRole('columnheader', { name: 'タイトル' })).not.toBeInTheDocument();
    expect(history.queryByRole('columnheader', { name: 'パス' })).not.toBeInTheDocument();
    expect(history.queryByRole('columnheader', { name: '資料' })).not.toBeInTheDocument();
  });

  it('方向を「送信」「受信」で出し、内訳は 0 を出さず、全部 0 の行は「—」にする', async () => {
    respond();
    await renderPage();
    const history = within(historyPanel());

    await history.findByText('MacBook Pro');
    const pushRow = within(history.getByRole('row', { name: /MacBook Pro/ }));
    expect(pushRow.getByText('送信')).toBeInTheDocument();
    expect(pushRow.getByText('追加 1')).toBeInTheDocument();
    // 0 の項目は出さない（意味を持つ 1 項目が 0 に埋もれないようにする）。
    expect(pushRow.queryByText('更新 0')).not.toBeInTheDocument();
    expect(pushRow.queryByText('削除 0')).not.toBeInTheDocument();

    const pullRow = within(history.getByRole('row', { name: /iPad/ }));
    expect(pullRow.getByText('受信')).toBeInTheDocument();
    // すべて 0 の行は空欄にせず「—」（取得漏れと区別する）。
    expect(pullRow.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('結果を色だけでなく文言でも示し、失敗行には次の行動が分かる文言を出す', async () => {
    respond();
    await renderPage();
    const history = within(historyPanel());

    await history.findByText('MacBook Pro');
    // ★ 陽性対照: 成功の行に「成功」、失敗の行に「失敗」の文言が出る（色だけに頼らない）。
    expect(
      within(history.getByRole('row', { name: /MacBook Pro/ })).getByText('成功'),
    ).toBeInTheDocument();
    const conflictRow = within(history.getByRole('row', { name: /会議室の PC/ }));
    expect(conflictRow.getByText('失敗')).toBeInTheDocument();
    expect(conflictRow.getByText('競合 1')).toBeInTheDocument();
    // 🔴 コードそのまま（`version_conflict`）を出さず、次の行動を指す文言にする。
    expect(conflictRow.getByText(/上の「同期の競合」から解決してください/)).toBeInTheDocument();
    expect(history.queryByText('version_conflict')).not.toBeInTheDocument();
  });

  it('件数と保持期間、および資料名を残さないことを同じ枠で告げる', async () => {
    respond();
    await renderPage();
    const history = within(historyPanel());

    const note = await history.findByText(/直近 50 件を表示します/);
    expect(note.textContent).toMatch(/記録は 3 年間保持されます/);
    expect(note.textContent).toMatch(/資料の名前は履歴に残しません/);
  });

  it('記録が無いときは空状態を出す（表の器だけを描かない）', async () => {
    respond({ history: [] });
    await renderPage();
    const history = within(historyPanel());

    expect(await history.findByText(/同期の記録はまだありません/)).toBeInTheDocument();
    expect(history.queryByRole('table')).not.toBeInTheDocument();
  });

  it('取得に失敗したら空状態へ縮退させず、再試行の導線を出す', async () => {
    respond({ historyFails: true });
    await renderPage();
    const history = within(historyPanel());

    expect(await history.findByText(/同期履歴を取得できませんでした/)).toBeInTheDocument();
    // 陰性の対: 「記録がありません」へ倒していない（倒すと本当に無いのか読めない）。
    expect(history.queryByText(/同期の記録はまだありません/)).not.toBeInTheDocument();

    // ★ 再試行を押すと実際に引き直す（導線が飾りでないことを固定する）。
    const before = historyCalls();
    respond();
    await userEvent.click(history.getByRole('button', { name: '再試行' }));
    await history.findByText('MacBook Pro');
    expect(historyCalls()).toBeGreaterThan(before);
  });
});
