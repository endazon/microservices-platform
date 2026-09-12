import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';
import { formatDateTime } from '@foundation/utils/formatDateTime';

// SC-20, UC-11, FR-20（#451）: Obsidian 連携設定画面。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（SC-18 / SC-21 と同じ作法）。
//
// 🔴 **否定形（管理者承認が無い・組織文書の同期導線が無い・平文が一覧に出ない）は陽性対照と対で置く。**
// 🔴 **時刻は固定する**（残り日数・期限切れの境界が「いま」に依存するため）。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc20ObsidianSettingsRoute } from '../routes/sc20ObsidianSettingsRoute';

const NOW = new Date('2026-08-28T00:00:00Z');

const ACTIVE_DEVICE = {
  id: '00000000-0000-0000-0000-0000000000a1',
  deviceName: 'MacBook Pro',
  issuedAt: '2026-08-20T00:00:00Z',
  expiresAt: '2026-09-19T00:00:00Z',
  revoked: false,
  lastSyncAt: '2026-08-27T22:10:00Z',
  active: true,
};

const EXPIRING_DEVICE = {
  ...ACTIVE_DEVICE,
  id: '00000000-0000-0000-0000-0000000000a2',
  deviceName: 'iPad',
  expiresAt: '2026-09-01T00:00:00Z',
};

const EXPIRED_DEVICE = {
  ...ACTIVE_DEVICE,
  id: '00000000-0000-0000-0000-0000000000a3',
  deviceName: '自宅 PC',
  expiresAt: '2026-08-10T00:00:00Z',
  active: false,
};

const REVOKED_DEVICE = {
  ...ACTIVE_DEVICE,
  id: '00000000-0000-0000-0000-0000000000a4',
  deviceName: '紛失した端末',
  revoked: true,
  active: false,
};

const ISSUED_TOKEN = {
  deviceId: '00000000-0000-0000-0000-0000000000b1',
  deviceName: '新しい端末',
  token: 'pnt_secret_value_0123456789',
  expiresAt: '2026-09-27T00:00:00Z',
};

const ALL_DEVICES = [ACTIVE_DEVICE, EXPIRING_DEVICE, EXPIRED_DEVICE, REVOKED_DEVICE];

// #1442: 同期対象フォルダ（主要素 3）と競合（主要素 5）。
const FOLDERS = [
  { path: '仕事/メモ', noteCount: 12, lastSyncAt: '2026-08-27T22:10:00Z' },
  { path: '日誌', noteCount: 3, lastSyncAt: null },
];

const CONFLICT_SUMMARY = {
  id: '00000000-0000-0000-0000-0000000000c1',
  noteId: '00000000-0000-0000-0000-0000000000d1',
  title: '設計メモ',
  vaultPath: '仕事/メモ/設計メモ.md',
  detectedAt: '2026-08-27T09:00:00Z',
  // 🔴 端末名は接続端末の一覧と**重ならない値**にする —— `deviceRow()` は行を名前の
  // 正規表現で引くので、同じ名前が競合の行にも現れると既存のテストが 2 行を掴む。
  deviceId: '00000000-0000-0000-0000-0000000000a9',
  deviceName: '会議室の PC',
  localBaseVersion: 3,
  serverVersion: 5,
};

const CONFLICT_DETAIL = {
  ...CONFLICT_SUMMARY,
  localContent: '端末で書いた本文',
  serverContent: 'サーバに保存されている本文',
};

// #1446: 同期履歴（主要素 6）。**本ファイルの既定は 0 件**である —— 履歴の行の描画は
// `SyncHistoryPanel.test.tsx` が固定しており、こちらへ行を足すと端末名の正規表現で行を引く
// 既存のテスト（`deviceRow()`）が 2 つの表に当たる。
function respond({
  devices = ALL_DEVICES as unknown[],
  folders = FOLDERS as unknown[],
  conflicts = [CONFLICT_SUMMARY] as unknown[],
  history = [] as unknown[],
}: {
  devices?: unknown[];
  folders?: unknown[];
  conflicts?: unknown[];
  history?: unknown[];
} = {}) {
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す）。
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    const method = init?.method ?? 'GET';
    if (path === '/private-notes/devices' && method === 'GET') {
      return Promise.resolve(jsonResponse(devices));
    }
    if (path === '/private-notes/devices' && method === 'POST') {
      return Promise.resolve(jsonResponse(ISSUED_TOKEN, 201));
    }
    if (path === '/private-notes/sync-settings') {
      return Promise.resolve(
        jsonResponse({ targetFolders: folders, updatedAt: '2026-08-27T00:00:00Z' }),
      );
    }
    if (path === '/private-notes/conflicts') return Promise.resolve(jsonResponse(conflicts));
    if (path === '/private-notes/sync-history') return Promise.resolve(jsonResponse(history));
    if (path.endsWith('/resolve')) {
      return Promise.resolve(
        jsonResponse({
          conflictId: CONFLICT_SUMMARY.id,
          noteId: CONFLICT_SUMMARY.noteId,
          resolution: 'local',
          noteVersion: 6,
          createdNoteId: null,
        }),
      );
    }
    if (path.startsWith('/private-notes/conflicts/')) {
      return Promise.resolve(jsonResponse(CONFLICT_DETAIL));
    }
    if (path.endsWith('/reissue')) return Promise.resolve(jsonResponse(ISSUED_TOKEN));
    if (path.endsWith('/revoke-all')) return Promise.resolve(jsonResponse({ revokedCount: 4 }));
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

async function renderPage() {
  return renderUnitRoute((shell) => [createSc20ObsidianSettingsRoute(shell)], {
    initialEntry: '/my/obsidian',
  });
}

/** 端末名からその行を引く。 */
const deviceRow = (name: string) => screen.getByRole('row', { name: new RegExp(name) });

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.setSystemTime(NOW);
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('SC-20 Obsidian 連携設定: 固定文言', () => {
  it('同期の範囲の固定文言を出す', async () => {
    respond();
    await renderPage();

    const text = await screen.findByText(/同期できるのは、あなたが作成した個人資料のみです/);
    expect(text.textContent).toMatch(/他の利用者の資料および組織文書は同期されません/);
    expect(text.textContent).toMatch(/公開範囲を変更しても同期は継続します/);
  });

  it('🔴 削除の説明に「90 日を過ぎると……復元できなくなります」を必ず含む', async () => {
    respond();
    await renderPage();

    const text = await screen.findByText(/Obsidian 側で削除した資料は/);
    expect(text.textContent).toMatch(/90\s*日を過ぎると自動的に完全削除され、復元できなくなります/);
    expect(text.textContent).toMatch(
      /週に一度お知らせし、完全削除の\s*7\s*日前にも改めてお知らせします/,
    );
  });

  it('業務関連資料としての扱いを、削除の説明とは別の段落で出す', async () => {
    respond();
    await renderPage();

    const handling = await screen.findByText(/同期した資料は業務関連資料として扱われます/);
    expect(handling.textContent).toMatch(/同期対象フォルダに入れた私的なメモも/);
    // 🔴 削除の説明と同じ段落にまとめない（別の性質の注意であり、まとめると読み飛ばされる）。
    expect(handling.textContent).not.toMatch(/90\s*日を過ぎると/);
  });

  it('期限切れトークンがプラグイン設定に残る旨の固定文言を出す', async () => {
    respond();
    await renderPage();

    const text = await screen.findByText(/有効期限が切れたトークンは/);
    expect(text.textContent).toMatch(/Obsidian\s*プラグインの設定には残ったままです/);
    expect(text.textContent).toMatch(/入れ直してください/);
  });
});

describe('SC-20 Obsidian 連携設定: 端末一覧', () => {
  it('🔴 4 状態を区別して表示する（有効／期限切れ間近／期限切れ／失効）', async () => {
    respond();
    await renderPage();

    expect(await screen.findByText('有効（残り 22 日）')).toBeInTheDocument();
    expect(screen.getByText('期限切れ間近（残り 4 日）')).toBeInTheDocument();
    expect(screen.getByText('期限切れ（同期は停止しています）')).toBeInTheDocument();
    expect(screen.getByText('失効済み')).toBeInTheDocument();
  });

  it('端末名と最終同期を出す', async () => {
    respond();
    await renderPage();

    expect(await screen.findByText('MacBook Pro')).toBeInTheDocument();
    // 整形は共通ヘルパが持つ（表示帯の規則をここで二重に定義しない）。
    expect(
      within(deviceRow('MacBook Pro')).getByText(formatDateTime(ACTIVE_DEVICE.lastSyncAt)),
    ).toBeInTheDocument();
  });

  it('🔴 個別失効は失効済み以外の全行にある（期限切れの行にも残す）', async () => {
    respond();
    await renderPage();

    await screen.findByText('MacBook Pro');
    for (const name of ['MacBook Pro', 'iPad', '自宅 PC']) {
      expect(
        within(deviceRow(name)).getByRole('button', { name: 'この端末を失効する' }),
      ).toBeInTheDocument();
    }
    // 失効済みの行には出さない（既に無効なので操作が意味を持たない）。
    expect(
      within(deviceRow('紛失した端末')).queryByRole('button', { name: 'この端末を失効する' }),
    ).not.toBeInTheDocument();
  });

  it('期限切れの行から同じ行で再発行できる', async () => {
    respond();
    await renderPage();

    await screen.findByText('自宅 PC');
    expect(
      within(deviceRow('自宅 PC')).getByRole('button', { name: '再発行する' }),
    ).toBeInTheDocument();
  });

  it('接続端末が無いときは空状態を出す', async () => {
    respond({ devices: [] });
    await renderPage();

    expect(await screen.findByText(/接続している端末はまだありません/)).toBeInTheDocument();
    // 陽性対照: 発行の導線は残る（空だと何もできない画面にはしない）。
    expect(screen.getByRole('button', { name: 'トークンを発行する' })).toBeEnabled();
  });

  it('一覧を引けないときはエラーを出す', async () => {
    mocks.apiRequest.mockRejectedValue(new Error('boom'));
    await renderPage();

    expect(await screen.findByText(/接続端末の一覧を取得できませんでした/)).toBeInTheDocument();
  });
});

describe('SC-20 Obsidian 連携設定: トークンの発行と再発行', () => {
  it('🔴 平文は発行応答にだけ現れ、再表示できない旨を同じ枠に出す', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('MacBook Pro');
    // 陰性の前提: 発行前は平文がどこにも無い。
    expect(screen.queryByText(ISSUED_TOKEN.token)).not.toBeInTheDocument();

    await user.type(screen.getByLabelText('端末名（任意）'), '新しい端末');
    await user.click(screen.getByRole('button', { name: 'トークンを発行する' }));

    expect(await screen.findByText(ISSUED_TOKEN.token)).toBeInTheDocument();
    expect(screen.getByText(/このトークンを表示できるのは今回だけです/)).toBeInTheDocument();
    expect(screen.getByText(/再表示できません（再発行のみ可能です）/)).toBeInTheDocument();
    expect(writes()[0]).toMatchObject({ path: '/private-notes/devices', method: 'POST' });
    expect(writes()[0].body).toEqual({ deviceName: '新しい端末' });
  });

  it('🔴 次の操作を始めると平文の表示は消える（画面に残さない）', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('MacBook Pro');
    await user.click(screen.getByRole('button', { name: 'トークンを発行する' }));
    expect(await screen.findByText(ISSUED_TOKEN.token)).toBeInTheDocument();

    await user.click(
      within(deviceRow('自宅 PC')).getByRole('button', { name: 'この端末を失効する' }),
    );
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: '失効する' }),
    );
    await waitFor(() => expect(screen.queryByText(ISSUED_TOKEN.token)).not.toBeInTheDocument());
  });

  it('再発行はその端末の口を叩き、平文を一度だけ出す', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('自宅 PC');
    await user.click(within(deviceRow('自宅 PC')).getByRole('button', { name: '再発行する' }));

    expect(await screen.findByText(ISSUED_TOKEN.token)).toBeInTheDocument();
    expect(writes()).toEqual([
      expect.objectContaining({
        path: `/private-notes/devices/${EXPIRED_DEVICE.id}/reissue`,
        method: 'POST',
      }),
    ]);
  });

  it('🔴 自動更新（リフレッシュ）の導線を置かない（陽性対照つき）', async () => {
    respond();
    await renderPage();

    // 陽性対照: 手動再発行は在る。
    expect(await screen.findAllByRole('button', { name: '再発行する' })).not.toHaveLength(0);
    expect(screen.queryByRole('button', { name: /自動更新/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /リフレッシュ/ })).not.toBeInTheDocument();
    expect(screen.getByText(/自動更新は行いません/)).toBeInTheDocument();
  });

  it('🔴 端末登録に管理者承認のステップが無い（陽性対照つき）', async () => {
    respond();
    await renderPage();

    // 陽性対照: 発行ボタンは押せる（承認待ちで塞がっていない）。
    expect(await screen.findByRole('button', { name: 'トークンを発行する' })).toBeEnabled();
    expect(screen.queryByText(/承認/)).not.toBeInTheDocument();
    expect(screen.queryByText(/申請/)).not.toBeInTheDocument();
  });

  it('🔴 組織文書・他利用者の設定への導線が無い（陽性対照つき）', async () => {
    respond();
    await renderPage();

    // 陽性対照: 自分の個人資料への導線は在る。
    expect(await screen.findByRole('link', { name: '個人資料の一覧へ' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /組織文書/ })).not.toBeInTheDocument();
    expect(screen.queryByText(/他の利用者の同期設定/)).not.toBeInTheDocument();
  });
});

describe('SC-20 Obsidian 連携設定: 失効', () => {
  it('個別失効は確認を経てから送られる', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('MacBook Pro');
    await user.click(
      within(deviceRow('MacBook Pro')).getByRole('button', { name: 'この端末を失効する' }),
    );

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/「MacBook Pro」のトークンを無効にします/)).toBeInTheDocument();
    // 押すまで要求は飛ばない。
    expect(writes()).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: '失効する' }));
    await waitFor(() =>
      expect(writes()).toEqual([
        expect.objectContaining({
          path: `/private-notes/devices/${ACTIVE_DEVICE.id}`,
          method: 'DELETE',
        }),
      ]),
    );
  });

  it('🔴 一括失効の確認に復元不可である旨と対象台数が出る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('MacBook Pro');
    await user.click(screen.getByRole('button', { name: 'すべての端末を失効する' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/この操作は元に戻せません/)).toBeInTheDocument();
    expect(within(dialog).getByText(/対象: 4 台/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'すべて失効する' }));
    await waitFor(() =>
      expect(writes()).toEqual([
        expect.objectContaining({ path: '/private-notes/devices/revoke-all', method: 'POST' }),
      ]),
    );
  });

  it('確認をやめると失効は送られない', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByText('MacBook Pro');
    await user.click(screen.getByRole('button', { name: 'すべての端末を失効する' }));
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'やめる' }),
    );

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });

  it('端末が 1 台も無ければ一括失効は押せない', async () => {
    respond({ devices: [] });
    await renderPage();

    await screen.findByText(/接続している端末はまだありません/);
    expect(screen.getByRole('button', { name: 'すべての端末を失効する' })).toBeDisabled();
  });
});

// 区画は `Panel` の `aria-label`（= role="region" のアクセシブル名）で引く。
// **要素を返し、問い合わせは呼び出し側で `within()` に包む** —— 問い合わせ済みの器を返すと
// `testing-library/prefer-screen-queries` が追えず error になる。
/** 同期対象範囲の区画。 */
const folderPanel = () => screen.getByRole('region', { name: '同期対象範囲' });
/** 同期の競合の区画。 */
const conflictPanel = () => screen.getByRole('region', { name: '同期の競合' });

describe('SC-20 Obsidian 連携設定: 同期対象範囲（#1442）', () => {
  it('フォルダのパス・配下の資料数・最終同期を出す', async () => {
    respond();
    await renderPage();

    expect(await screen.findByRole('cell', { name: '仕事/メモ' })).toBeInTheDocument();
    const panel = within(folderPanel());
    expect(panel.getByRole('columnheader', { name: '配下の資料数' })).toBeInTheDocument();
    expect(panel.getByRole('cell', { name: '12' })).toBeInTheDocument();
    expect(
      within(panel.getByRole('row', { name: /仕事\/メモ/ })).getByText(
        formatDateTime(FOLDERS[0].lastSyncAt),
      ),
    ).toBeInTheDocument();
  });

  it('フォルダ未設定は「すべての個人資料が対象」と明言する（異常として見せない）', async () => {
    respond({ folders: [] });
    await renderPage();

    expect(await screen.findByText(/同期対象フォルダを指定していません/)).toBeInTheDocument();
    expect(screen.getByText(/いまは、あなたの個人資料がすべて同期の対象です/)).toBeInTheDocument();
    // 陽性対照: 空でも追加の導線は残る（何もできない画面にしない）。
    expect(within(folderPanel()).getByLabelText('追加するフォルダのパス')).toBeInTheDocument();
  });

  it('フォルダを追加すると、いまの一覧に足した全量を送る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '仕事/メモ' });
    await user.type(within(folderPanel()).getByLabelText('追加するフォルダのパス'), '/読書メモ/');
    await user.click(within(folderPanel()).getByRole('button', { name: 'フォルダを追加する' }));

    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0]).toMatchObject({ path: '/private-notes/sync-settings', method: 'PUT' });
    // 前後の `/` を落とした形で、既存の 2 件と併せた全量を送る（差分の口は無い）。
    expect(writes()[0].body).toEqual({ targetFolders: ['仕事/メモ', '日誌', '読書メモ'] });
  });

  it('空・重複のパスでは追加ボタンを押せない（表記違いの重複も含む）', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '仕事/メモ' });
    const add = () => within(folderPanel()).getByRole('button', { name: 'フォルダを追加する' });
    expect(add()).toBeDisabled();

    await user.type(within(folderPanel()).getByLabelText('追加するフォルダのパス'), '/仕事/メモ/');
    expect(add()).toBeDisabled();

    // 陽性対照: 別のフォルダなら押せる（常に無効な実装と区別する）。
    await user.clear(within(folderPanel()).getByLabelText('追加するフォルダのパス'));
    await user.type(within(folderPanel()).getByLabelText('追加するフォルダのパス'), '読書メモ');
    expect(add()).toBeEnabled();
  });

  it('🔴 「対象から外す」の確認に「削除ではない・資料は消えない」が出る', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '仕事/メモ' });
    await user.click(
      within(within(folderPanel()).getByRole('row', { name: /仕事\/メモ/ })).getByRole('button', {
        name: '対象フォルダから外す',
      }),
    );

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/これは削除ではありません/)).toBeInTheDocument();
    expect(
      within(dialog).getByText(
        /フォルダ配下の資料はサーバに残り、Obsidian\s*との同期が止まるだけです/,
      ),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/対象: 仕事\/メモ/)).toBeInTheDocument();
    // 押すまで要求は飛ばない。
    expect(writes()).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: '対象から外す' }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    // 外した 1 件を除いた全量を送る（削除の口は叩かない）。
    expect(writes()[0]).toMatchObject({ path: '/private-notes/sync-settings', method: 'PUT' });
    expect(writes()[0].body).toEqual({ targetFolders: ['日誌'] });
    expect(writes().map((c) => c.method)).not.toContain('DELETE');
  });

  it('🔴 業務関連資料の固定文言がフォルダ指定 UI と同じ区画にある', async () => {
    respond();
    await renderPage();

    await screen.findByRole('cell', { name: '仕事/メモ' });
    const panel = within(folderPanel());
    // 区画の中に「追加の入力欄」と「固定文言」の両方があること（＝隣に置かれている）。
    expect(panel.getByLabelText('追加するフォルダのパス')).toBeInTheDocument();
    expect(panel.getByText(/同期した資料は業務関連資料として扱われます/)).toBeInTheDocument();
    // 陰性の対: 画面上部（区画の外）には残していない（2 箇所に置くと片方が古くなる）。
    expect(screen.getAllByText(/同期した資料は業務関連資料として扱われます/)).toHaveLength(1);
  });
});

describe('SC-20 Obsidian 連携設定: 競合の解決（#1442）', () => {
  it('未解決の競合をタイトル・パス・検出日時・端末・版とともに一覧する', async () => {
    respond();
    await renderPage();

    expect(await screen.findByRole('cell', { name: '設計メモ' })).toBeInTheDocument();
    const panel = within(conflictPanel());
    expect(panel.getByRole('cell', { name: '仕事/メモ/設計メモ.md' })).toBeInTheDocument();
    expect(panel.getByRole('cell', { name: '会議室の PC' })).toBeInTheDocument();
    expect(panel.getByText('ローカル 3 版 ／ サーバ 5 版')).toBeInTheDocument();
    expect(panel.getByText(formatDateTime(CONFLICT_SUMMARY.detectedAt))).toBeInTheDocument();
  });

  it('競合が無いときは「競合はありません」を出す', async () => {
    respond({ conflicts: [] });
    await renderPage();

    expect(await screen.findByText('競合はありません。')).toBeInTheDocument();
  });

  it('🔴 一覧は本文を持たず、行を選んで初めて両版の本文を引く', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '設計メモ' });
    // 選ぶ前は本文の問い合わせもしていない（一覧を重くしない）。
    expect(mocks.apiRequest.mock.calls.map((c) => c[0])).not.toContain(
      `/private-notes/conflicts/${CONFLICT_SUMMARY.id}`,
    );
    expect(screen.queryByText('端末で書いた本文')).not.toBeInTheDocument();

    await user.click(within(conflictPanel()).getByRole('button', { name: '本文を見て解決する' }));

    // 2 ペイン（ローカル版／サーバ版）が並ぶ。
    expect(await screen.findByLabelText('ローカル版の本文')).toHaveTextContent('端末で書いた本文');
    expect(screen.getByLabelText('サーバ版の本文')).toHaveTextContent('サーバに保存されている本文');
  });

  it('🔴 3 択だけを置き、自動解決の選択肢を置かない（陽性対照つき）', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '設計メモ' });
    await user.click(within(conflictPanel()).getByRole('button', { name: '本文を見て解決する' }));

    // 陽性対照: 3 択は在る（何も描かない実装と区別する）。
    const resolution = within(await screen.findByRole('region', { name: '競合の解決' }));
    expect(resolution.getByRole('button', { name: 'ローカルを採用' })).toBeInTheDocument();
    expect(resolution.getByRole('button', { name: 'サーバを採用' })).toBeInTheDocument();
    expect(resolution.getByRole('button', { name: '両方を残す（別名保存）' })).toBeInTheDocument();
    // 陰性: 自動解決（後勝ち・一括解決）の択は無い（ADR-0037 決定 7）。
    expect(screen.queryByRole('button', { name: /自動/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /後勝ち/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /すべて解決/ })).not.toBeInTheDocument();
  });

  it.each([
    ['ローカルを採用', 'ローカルを採用する', 'local', /端末の本文が新しい版になります/],
    ['サーバを採用', 'サーバを採用する', 'server', /端末側の本文は失われます/],
    ['両方を残す（別名保存）', '両方を残す', 'both', /別名の資料として新しく作ります/],
  ])('%s は確認を経てから %s として送られる', async (button, confirmLabel, resolution, body) => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '設計メモ' });
    await user.click(within(conflictPanel()).getByRole('button', { name: '本文を見て解決する' }));
    await user.click(
      within(await screen.findByRole('region', { name: '競合の解決' })).getByRole('button', {
        name: button,
      }),
    );

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(body)).toBeInTheDocument();
    expect(within(dialog).getByText(/対象: 「設計メモ」/)).toBeInTheDocument();
    // 押すまで要求は飛ばない（確認が確認として機能している）。
    expect(writes()).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: confirmLabel }));
    await waitFor(() => expect(writes()).toHaveLength(1));
    expect(writes()[0]).toMatchObject({
      path: `/private-notes/conflicts/${CONFLICT_SUMMARY.id}/resolve`,
      method: 'POST',
    });
    expect(writes()[0].body).toEqual({ resolution });
  });

  it('確認をやめると解決は送られない', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '設計メモ' });
    await user.click(within(conflictPanel()).getByRole('button', { name: '本文を見て解決する' }));
    await user.click(
      within(await screen.findByRole('region', { name: '競合の解決' })).getByRole('button', {
        name: 'サーバを採用',
      }),
    );
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'やめる' }),
    );

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(writes()).toHaveLength(0);
  });

  it('解決したら競合の一覧を引き直し、詳細を閉じる', async () => {
    respond();
    const user = userEvent.setup();
    await renderPage();

    await screen.findByRole('cell', { name: '設計メモ' });
    const before = mocks.apiRequest.mock.calls.filter(
      (c) => c[0] === '/private-notes/conflicts',
    ).length;

    await user.click(within(conflictPanel()).getByRole('button', { name: '本文を見て解決する' }));
    await user.click(
      within(await screen.findByRole('region', { name: '競合の解決' })).getByRole('button', {
        name: 'ローカルを採用',
      }),
    );
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'ローカルを採用する' }),
    );

    await waitFor(() =>
      expect(
        mocks.apiRequest.mock.calls.filter((c) => c[0] === '/private-notes/conflicts').length,
      ).toBeGreaterThan(before),
    );
    // 解決したら 2 ペインを閉じる（解決済みの本文を並べたままにしない）。
    await waitFor(() =>
      expect(screen.queryByRole('region', { name: '競合の解決' })).not.toBeInTheDocument(),
    );
    expect(
      await screen.findByText(/ローカルの本文を新しい版として保存しました/),
    ).toBeInTheDocument();
    //
    // 🔴 **個人資料の一覧（SC-19）の無効化はここでは観測できない。** 本画面には当該問い合わせの
    // 購読者が居らず、TanStack Query は購読の無い問い合わせを即座に引き直さない（stale にするだけ）。
    // 無効化の配線そのものは `api/useSyncConflicts.ts` に在り、効き目は SC-19 を開いたときに現れる。
    // **「呼ばれていない」ことをここで陰性として書かない** —— 配線の有無と区別が付かないためである。
  });
});
