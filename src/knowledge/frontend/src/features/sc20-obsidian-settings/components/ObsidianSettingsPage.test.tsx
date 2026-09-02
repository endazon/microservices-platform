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

function respond({ devices = ALL_DEVICES as unknown[] }: { devices?: unknown[] } = {}) {
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す）。
  mocks.apiRequest.mockImplementation((path: string, init?: { method?: string }) => {
    const method = init?.method ?? 'GET';
    if (path === '/private-notes/devices' && method === 'GET') {
      return Promise.resolve(jsonResponse(devices));
    }
    if (path === '/private-notes/devices' && method === 'POST') {
      return Promise.resolve(jsonResponse(ISSUED_TOKEN, 201));
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
