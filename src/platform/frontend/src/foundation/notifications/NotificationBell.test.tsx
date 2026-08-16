import { describe, it, expect, vi, beforeEach } from 'vitest';
import { I18nProvider } from '@lingui/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { i18n } from '@foundation/i18n';
import { jsonResponse } from '@foundation/testing/bffResponse';
import { NotificationBell } from './NotificationBell';

// FR-22, IADR-0215 決定 2 / テスト仕様書 T-03〜T-07。
//
// **起点 ID は FR-22 だけを書く。** 本 PR が実装したのは通知の受け皿だけであり、
// 同じユースケースに属する他の機能（個人資料・Obsidian 同期）は #451 で保留中である。
// 保留中の機能を含む ID をテストへ書くと `check-test-traceability.js` が
// 「実装先行・仕様書なし」としてその ID のテスト仕様書を要求するが、
// 保留中の機能のテスト仕様書は今は書けない。**保留対象の ID は、その機能に着手する issue が
// 初めて書く**（`scripts/test-traceability-allowlist.json` の `$comment_specMissing`
// ［2026-08-05 / #502］の運用に揃える。**allowlist で黙らせない**）。
// ユースケースとの対応は `docs/functional/FR-22` と `docs/tests/FR-22` が持つ。
//
// 通信は orval 生成コードの経路（`bffFetch` → **`apiRequest`**）を通るので、モックは
// `apiRequest` に当てる（IADR-0135 決定 4）。**手書きの HTTP クライアントは使っていない。**
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

const LIST = {
  items: [
    {
      id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      kind: 'private-note-purge-imminent',
      count: 2,
      deadline: '2026-08-23T00:00:00Z',
      occurredAt: '2026-08-16T01:00:00Z',
      read: false,
    },
    {
      id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
      kind: 'storage-quota-warning',
      thresholdPercent: 95,
      occurredAt: '2026-08-15T01:00:00Z',
      read: false,
    },
    {
      id: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
      kind: 'sync-token-expiry',
      count: 1,
      deadline: '2026-08-22T00:00:00Z',
      occurredAt: '2026-08-14T01:00:00Z',
      read: true,
    },
  ],
  unreadCount: 2,
};

function renderBell() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
  return render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <NotificationBell />
      </QueryClientProvider>
    </I18nProvider>,
  );
}

/** ベルのボタン（未読件数はアクセシブルネームにも入っている）。 */
function bell() {
  return screen.getByRole('button', { name: /通知（未読/ });
}

function panel() {
  return screen.getByRole('region', { name: '通知一覧' });
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('NotificationBell (FR-22)', () => {
  // T-03: 未読件数と一覧。
  it('未読件数を出し、開くと通知が並ぶ', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(LIST));
    const user = userEvent.setup();
    renderBell();

    // 未読件数はバッジの数字だけでなくアクセシブルネームにも入る。
    expect(await screen.findByRole('button', { name: '通知（未読 2 件）' })).toBeInTheDocument();

    await user.click(bell());
    expect(within(panel()).getAllByRole('listitem')).toHaveLength(3);
  });

  // T-04 / INDEX 決定 21: 色だけで意味を持たせない（アイコン ＋ テキストのラベルが常に付く）。
  it('各行にテキストのラベルが付く（色だけで意味を持たせない）', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(LIST));
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: /通知（未読/ }));

    const rows = within(panel()).getAllByRole('listitem');
    // 7 日前通知 = 注意 / 容量 95% = 重要 / 同期トークン = 注意。
    expect(rows[0]).toHaveTextContent('注意');
    expect(rows[1]).toHaveTextContent('重要');
    expect(rows[2]).toHaveTextContent('注意');
  });

  // ★ FR-22 受け入れ基準（契約の側）: 資料のタイトル・本文は契約に無いので画面にも出ようが無い。
  it('件数と期限だけが出る（資料のタイトル・本文は現れない）', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(LIST));
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: /通知（未読/ }));

    expect(panel()).toHaveTextContent('個人資料 2 件');
    expect(panel()).toHaveTextContent('保存容量が上限の 95% に達しました');
  });

  // T-05: 既読化（冪等な POST）。応答の unreadCount を使い、一覧を取り直す。
  it('既読にすると既読化が呼ばれ、未読件数が更新される', async () => {
    mocks.apiRequest
      .mockResolvedValueOnce(jsonResponse(LIST))
      .mockResolvedValueOnce(jsonResponse({ id: LIST.items[0].id, unreadCount: 1 }))
      .mockResolvedValue(
        jsonResponse({
          items: [{ ...LIST.items[0], read: true }, LIST.items[1], LIST.items[2]],
          unreadCount: 1,
        }),
      );
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: /通知（未読/ }));

    await user.click(within(panel()).getAllByRole('button', { name: '既読にする' })[0]);

    // `bffFetch` は `/bff` 接頭辞を外して `apiRequest` へ渡す（実行時 config が前置するため）。
    await waitFor(() => {
      expect(
        mocks.apiRequest.mock.calls.some(([url]) =>
          String(url).includes(`/notifications/${LIST.items[0].id}/read`),
        ),
      ).toBe(true);
    });
    expect(await screen.findByRole('button', { name: '通知（未読 1 件）' })).toBeInTheDocument();
  });

  // T-06: 空であることを明示する（無言で何も描かない形にしない）。
  it('通知が 0 件のとき「通知はありません」を出す', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse({ items: [], unreadCount: 0 }));
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: '通知（未読 0 件）' }));

    expect(within(panel()).getByText('通知はありません')).toBeInTheDocument();
    expect(within(panel()).queryAllByRole('listitem')).toHaveLength(0);
  });

  // T-07: 取得失敗の縮退。共通シェルは壊れない。
  it('取得に失敗しても壊れず、失敗を色 ＋ アイコン ＋ テキストで伝える', async () => {
    mocks.apiRequest.mockRejectedValue(new ApiError('server', 'boom', 500));
    const user = userEvent.setup();
    renderBell();

    // ベル自体は描かれ続ける（未読件数は 0 として扱う）。
    await user.click(await screen.findByRole('button', { name: '通知（未読 0 件）' }));
    const alert = await within(panel()).findByRole('alert');
    expect(alert).toHaveTextContent('エラー');
    expect(alert).toHaveTextContent('通知を取得できませんでした');
  });

  // 既読化の失敗も一覧を壊さない。
  it('既読化に失敗しても一覧は残り、失敗が伝わる', async () => {
    mocks.apiRequest
      .mockResolvedValueOnce(jsonResponse(LIST))
      .mockRejectedValue(new ApiError('server', 'boom', 500));
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: /通知（未読/ }));
    await user.click(within(panel()).getAllByRole('button', { name: '既読にする' })[0]);

    expect(await within(panel()).findByRole('alert')).toHaveTextContent('既読にできませんでした');
    expect(within(panel()).getAllByRole('listitem')).toHaveLength(3);
  });

  // 開閉。閉じているときに一覧を描かない（シェルの見た目を占有しない）。
  it('もう一度押すと閉じる', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(LIST));
    const user = userEvent.setup();
    renderBell();
    await user.click(await screen.findByRole('button', { name: /通知（未読/ }));
    expect(panel()).toBeInTheDocument();

    await user.click(bell());
    expect(screen.queryByRole('region', { name: '通知一覧' })).not.toBeInTheDocument();
  });
});
