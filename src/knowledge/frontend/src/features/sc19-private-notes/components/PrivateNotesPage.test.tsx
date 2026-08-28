import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-19, UC-11, FR-19/FR-21（#451）: 個人資料管理画面。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（SC-18 / SC-21 と同じ作法）。
//
// 🔴 **否定形（本文編集欄が無い・管理者導線が無い・他人の資料の件数が出ない）は陽性対照と対で置く。**
// 何も描かない実装でも否定形だけなら緑になるためである。変異試験の結果は作業仕様書に残す。
//
// 🔴 **時刻は固定する。** 残り日数・警告色の境界は「いま」に依存するので、実時間のままだと
// 日付をまたいだ瞬間に赤くなるテストになる。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc19PrivateNotesRoute } from '../routes/sc19PrivateNotesRoute';

const NOW = new Date('2026-08-28T00:00:00Z');
const GB = 1024 ** 3;

const LIVE_NOTE = {
  id: '00000000-0000-0000-0000-000000000001',
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
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-20T09:30:00Z',
};

/** 完全削除まで 3 日（残り 7 日以内＝警告色）。 */
const TRASHED_URGENT = {
  ...LIVE_NOTE,
  id: '00000000-0000-0000-0000-000000000002',
  title: '古い議事録',
  vaultPath: '古い議事録.md',
  bytes: 0.12 * GB,
  deleted: true,
  deletedAt: '2026-06-02T00:00:00Z',
  purgeAt: '2026-08-31T00:00:00Z',
};

/** 完全削除まで 60 日（警告色ではない）。 */
const TRASHED_CALM = {
  ...TRASHED_URGENT,
  id: '00000000-0000-0000-0000-000000000003',
  title: '下書き',
  vaultPath: '下書き.md',
  bytes: 0.08 * GB,
  purgeAt: '2026-10-27T00:00:00Z',
};

function usage(percent: number) {
  return { usedBytes: (percent / 100) * GB, limitBytes: GB, percent };
}

/** BFF の面へ応答を割り当てる。既定は「利用中 1 件＋削除済み 2 件」。 */
function respond({
  notes = [LIVE_NOTE, TRASHED_URGENT, TRASHED_CALM] as unknown[],
  quota = usage(20),
}: { notes?: unknown[]; quota?: ReturnType<typeof usage> } = {}) {
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す）。
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    if (path === '/private-notes' && (init?.method ?? 'GET') === 'GET') {
      return Promise.resolve(jsonResponse({ usage: quota, notes }));
    }
    if (path.endsWith('/purge')) {
      return Promise.resolve(jsonResponse({ purgedCount: 1, freedBytes: 1 }));
    }
    return Promise.resolve(jsonResponse({}));
  });
}

/** apiRequest が受けた書き込みの呼び出し（GET を除く）。 */
function writes(): { path: string; method: string; body: unknown }[] {
  return mocks.apiRequest.mock.calls
    .map((c) => ({
      path: c[0] as string,
      method: (c[1] as { method?: string } | undefined)?.method ?? 'GET',
      body: (c[1] as { body?: string } | undefined)?.body,
    }))
    .filter((c) => c.method !== 'GET')
    .map((c) => ({ ...c, body: typeof c.body === 'string' ? JSON.parse(c.body) : c.body }));
}

async function renderPage(initialEntry = '/my/notes') {
  return renderUnitRoute((shell) => [createSc19PrivateNotesRoute(shell)], { initialEntry });
}

/** 本文（tbody）の行。 */
const bodyRows = () => within(screen.getAllByRole('rowgroup')[1]).getAllByRole('row');

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(NOW);
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('SC-19 個人資料管理: 一覧と固定文言', () => {
  it('画面ラベルが「個人資料」であり、「マイスペース」「個人メモ」を使わない', async () => {
    respond();
    await renderPage();

    expect(await screen.findByRole('heading', { name: '個人資料' })).toBeInTheDocument();
    // 否定形の対（上の陽性対照が無いと、何も描かない実装でも緑になる）。
    expect(screen.queryByText(/マイスペース/)).not.toBeInTheDocument();
    expect(screen.queryByText(/個人メモ/)).not.toBeInTheDocument();
  });

  it('各行に 👤 と「個人資料（自分のみ）」が付く（色だけで意味を持たせない）', async () => {
    respond();
    await renderPage();

    expect(await screen.findByText('設計メモ')).toBeInTheDocument();
    expect(screen.getAllByText('👤 個人資料（自分のみ）').length).toBeGreaterThan(0);
  });

  it('業務関連資料としての扱いの固定文言が常時表示される（折りたたまない）', async () => {
    respond();
    await renderPage();

    const notice = await screen.findByText(/個人資料は業務関連資料として扱われます/);
    expect(notice).toBeInTheDocument();
    expect(notice.textContent).toMatch(/退職日から 30\s*日間、管理者が閲覧することがあります/);
    // 折りたたみの中に入れていないこと（details / summary を使っていない）。
    expect(notice.closest('details')).toBeNull();
  });

  it('本文の編集欄も編集導線も無く、Obsidian 連携への導線がある（陽性対照つき）', async () => {
    respond();
    await renderPage();

    // 陽性対照: 作成フォームは在る（何も描かない実装と区別する）。
    expect(await screen.findByRole('button', { name: '作成する' })).toBeInTheDocument();
    // 否定形: 本文の入力欄（textarea）は 1 つも無い。
    expect(screen.queryAllByRole('textbox').map((el) => el.tagName)).not.toContain('TEXTAREA');
    expect(screen.queryByRole('button', { name: /本文を編集/ })).not.toBeInTheDocument();
    // 本文を書く経路は Obsidian 連携である。
    expect(screen.getByRole('link', { name: 'Obsidian 連携設定へ' })).toHaveAttribute(
      'href',
      '/my/obsidian',
    );
  });

  it('管理者導線・他人の資料を示唆する表示が無い（陽性対照つき）', async () => {
    respond();
    await renderPage();

    // 陽性対照: 自分の資料は見えている。
    expect(await screen.findByText('設計メモ')).toBeInTheDocument();
    expect(screen.queryByText(/閲覧できません/)).not.toBeInTheDocument();
    expect(screen.queryByText(/他 \d+ 件/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /上限を引き上げ/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /全利用者/ })).not.toBeInTheDocument();
  });

  it('空状態を描く（作成導線つき）', async () => {
    respond({ notes: [] });
    await renderPage();

    expect(await screen.findByText(/個人資料がまだありません/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '作成する' })).toBeInTheDocument();
  });

  it('タイトルで絞り込め、条件が URL に載る', async () => {
    respond();
    const user = userEvent.setup();
    const { router } = await renderPage();

    await screen.findByText('設計メモ');
    await user.type(screen.getByLabelText('タイトルで絞り込む'), '設計');

    await waitFor(() => expect(router.state.location.search).toMatchObject({ q: '設計' }));
    expect(bodyRows()).toHaveLength(1);
  });
});

describe('SC-19 個人資料管理: 保存容量', () => {
  it('使用量・上限・「うち削除済み」の内訳を同時に出す', async () => {
    respond({ quota: usage(20) });
    await renderPage();

    // 削除済み 2 件（0.12 + 0.08 = 0.20 GB）を画面が合算する。
    expect(
      await screen.findByText(/0\.20 \/ 1\.00 GB（うち削除済み 0\.20 GB）/),
    ).toBeInTheDocument();
    expect(screen.getByText(/版履歴/)).toBeInTheDocument();
  });

  it.each([
    [50, null],
    [85, /80% を超えました/],
    [96, /95% を超えました/],
    [100, /保存容量の上限に達しました/],
  ])('使用率 %s%% の警告が段階的に変わる', async (percent, pattern) => {
    respond({ quota: usage(percent) });
    await renderPage();

    await screen.findByText('設計メモ');
    if (pattern === null) {
      expect(screen.queryByText(/80% を超えました/)).not.toBeInTheDocument();
      expect(screen.queryByText(/95% を超えました/)).not.toBeInTheDocument();
      expect(screen.queryByText(/上限に達しました/)).not.toBeInTheDocument();
      return;
    }
    expect(screen.getByText(pattern)).toBeInTheDocument();
  });

  it('95% のときに 80% の予告を並べない（強い警告が埋もれない）', async () => {
    respond({ quota: usage(96) });
    await renderPage();

    expect(await screen.findByText(/95% を超えました/)).toBeInTheDocument();
    expect(screen.queryByText(/80% を超えました/)).not.toBeInTheDocument();
  });

  it('100% では新規作成のみを拒否し、固定文言 2 段落を出す（既存資料の操作は残る）', async () => {
    respond({ quota: usage(100) });
    await renderPage();

    await screen.findByText('設計メモ');
    expect(screen.getByRole('button', { name: '作成する' })).toBeDisabled();
    expect(screen.getByLabelText('タイトル*必須')).toBeDisabled();
    expect(
      screen.getByText(/新しい資料は作成できませんが、編集中の資料は保存できます/),
    ).toBeInTheDocument();
    expect(screen.getByText(/削除済みの資料は 90\s*日間は容量に含まれます/)).toBeInTheDocument();
    // 陽性対照: 既存資料の削除ボタンは生きている（上限到達で画面全体が固まらない）。
    expect(screen.getByRole('button', { name: '削除する' })).toBeEnabled();
  });
});

describe('SC-19 個人資料管理: 作成と論理削除', () => {
  it('タイトルだけで作成でき、本文を送らない', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('設計メモ');
    await user.type(screen.getByLabelText('タイトル*必須'), '新しいメモ');
    await user.click(screen.getByRole('button', { name: '作成する' }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    const [call] = writes();
    expect(call).toMatchObject({ path: '/private-notes', method: 'POST' });
    expect(call.body).toEqual({ title: '新しいメモ', vaultPath: null });
    // 🔴 本文を運ぶ項目を送らない（ADR-0046 D-02）。
    expect(Object.keys(call.body as object)).not.toContain('content');
    expect(Object.keys(call.body as object)).not.toContain('body');
  });

  it('タイトルが空のままでは作成できない', async () => {
    respond();
    await renderPage();

    await screen.findByText('設計メモ');
    expect(screen.getByRole('button', { name: '作成する' })).toBeDisabled();
  });

  it('🔴 論理削除の確認に「容量は空きません（90 日間保管）」が出る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('設計メモ');
    await user.click(screen.getByRole('button', { name: '削除する' }));

    const dialog = await screen.findByRole('dialog');
    expect(
      within(dialog).getByText(/削除しても容量は空きません（90\s*日間保管されます）/),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/「設計メモ」を削除します/)).toBeInTheDocument();
    // 押すまで要求は飛ばない（確認が確認として機能している）。
    expect(writes()).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: '削除する' }));
    await waitFor(() =>
      expect(writes()).toEqual([
        expect.objectContaining({ path: `/private-notes/${LIVE_NOTE.id}`, method: 'DELETE' }),
      ]),
    );
  });

  it('確認をやめると要求は飛ばない', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('設計メモ');
    await user.click(screen.getByRole('button', { name: '削除する' }));
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'やめる' }),
    );

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });
});

describe('SC-19 個人資料管理: 削除済みタブ', () => {
  it('削除済みの件数バッジが一覧の件数と一致する', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    expect(await screen.findByRole('tab', { name: '削除済み（2）' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: '削除済み（2）' }));
    await waitFor(() => expect(bodyRows()).toHaveLength(2));
  });

  it('URL の ?tab=trash で削除済みを直接開ける', async () => {
    respond();
    await renderPage('/my/notes?tab=trash');

    expect(await screen.findByText('古い議事録')).toBeInTheDocument();
    expect(screen.queryByText('設計メモ')).not.toBeInTheDocument();
  });

  it('未知のタブ名は既定（利用中）へ倒す', async () => {
    respond();
    await renderPage('/my/notes?tab=archived');

    expect(await screen.findByText('設計メモ')).toBeInTheDocument();
    expect(screen.queryByText('古い議事録')).not.toBeInTheDocument();
  });

  it('残り日数を出し、7 日以内だけを警告色（＋文言）にする（境界の両側）', async () => {
    respond();
    await renderPage('/my/notes?tab=trash');

    // 3 日後に完全削除される行だけが「まもなく」を持つ。
    expect(await screen.findByText('まもなく完全削除（残り 3 日）')).toBeInTheDocument();
    // 60 日後の行は通常表示である（常に警告を出す実装と区別する陽性対照）。
    expect(screen.getByText('残り 60 日')).toBeInTheDocument();
  });

  it('削除日時を出す（更新日時ではない）', async () => {
    respond();
    await renderPage('/my/notes?tab=trash');

    expect(await screen.findByRole('columnheader', { name: '削除日時' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: '更新日時' })).not.toBeInTheDocument();
  });

  it('単票の復元ができる', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage('/my/notes?tab=trash');

    await screen.findByText('古い議事録');
    await user.click(within(bodyRows()[0]).getByRole('button', { name: '復元する' }));

    await waitFor(() =>
      expect(writes()).toEqual([
        expect.objectContaining({
          path: `/private-notes/${TRASHED_URGENT.id}/restore`,
          method: 'POST',
        }),
      ]),
    );
  });

  it('🔴 完全削除の確認に ①復元不可 ②90 日待てば自動 ③解放される容量 が出る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage('/my/notes?tab=trash');

    await screen.findByText('古い議事録');
    await user.click(within(bodyRows()[0]).getByRole('button', { name: '完全に削除する' }));

    const dialog = await screen.findByRole('dialog');
    expect(
      within(dialog).getByText(/この操作は元に戻せません。削除後はいかなる方法でも復元できません/),
    ).toBeInTheDocument();
    // ADR-0057 決定 4 の暫定手段（実体削除が未配備であることを伏せない）。
    expect(
      within(dialog).getByText(/削除の反映には時間がかかる場合があります/),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/90\s*日待てば自動的に完全削除されます/)).toBeInTheDocument();
    // ADR-0037 決定 20（波 3 監査の是正）: 単位は自動選択され GB 未満は MB で出る。
    expect(within(dialog).getByText(/解放される容量: 122\.88 MB/)).toBeInTheDocument();
  });

  it('一括選択で復元と完全削除の両方ができる', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage('/my/notes?tab=trash');

    await screen.findByText('古い議事録');
    // 選ぶ前は一括操作を実行できない。
    expect(screen.getByRole('button', { name: '選択した資料を復元する' })).toBeDisabled();

    await user.click(screen.getByLabelText('古い議事録 を選択'));
    await user.click(screen.getByLabelText('下書き を選択'));

    // 一括完全削除: 選択の合計容量が確認に出る（0.12 + 0.08 GB = 204.80 MB。単位は自動選択）。
    await user.click(screen.getByRole('button', { name: '選択した資料を完全に削除する' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/対象: 2 件/)).toBeInTheDocument();
    expect(within(dialog).getByText(/解放される容量: 204\.80 MB/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: '完全に削除する' }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0]).toMatchObject({ path: '/private-notes/purge', method: 'POST' });
    expect(writes()[0].body).toEqual({ ids: [TRASHED_URGENT.id, TRASHED_CALM.id] });
  });

  it('一括復元は選択した件数だけ復元を送る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage('/my/notes?tab=trash');

    await screen.findByText('古い議事録');
    await user.click(screen.getByLabelText('古い議事録 を選択'));
    await user.click(screen.getByLabelText('下書き を選択'));
    await user.click(screen.getByRole('button', { name: '選択した資料を復元する' }));

    await waitFor(() => expect(writes()).toHaveLength(2));
    expect(writes().map((c) => c.path)).toEqual([
      `/private-notes/${TRASHED_URGENT.id}/restore`,
      `/private-notes/${TRASHED_CALM.id}/restore`,
    ]);
  });
});

describe('SC-19 個人資料管理: 露出 3 トグル', () => {
  it('3 つを独立に設定でき、既定はいずれも OFF である', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('設計メモ');
    const search = screen.getByLabelText('横断検索に含める');
    const graph = screen.getByLabelText('ナレッジグラフに表示する');
    const ai = screen.getByLabelText('AI の入力に含める');
    expect(search).not.toBeChecked();
    expect(graph).not.toBeChecked();
    expect(ai).not.toBeChecked();

    await user.click(graph);

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0]).toMatchObject({
      path: `/private-notes/${LIVE_NOTE.id}/exposure`,
      method: 'PUT',
    });
    // 🔴 変えた 1 つだけが真になり、他の 2 つは現在値のまま送られる（連動させない）。
    expect(writes()[0].body).toEqual({
      includeInSearch: false,
      includeInGraph: true,
      includeInAi: false,
    });
  });
});

describe('SC-19 個人資料管理: 取得失敗', () => {
  it('一覧を引けないときは空状態ではなくエラーを出す', async () => {
    mocks.apiRequest.mockRejectedValue(new Error('boom'));
    await renderPage();

    expect(await screen.findByText(/一覧を取得できませんでした/)).toBeInTheDocument();
  });
});
