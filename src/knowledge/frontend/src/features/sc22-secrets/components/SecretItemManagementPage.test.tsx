import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-22, FR-05, ADR-0095 決定 1・4, ADR-0042 決定 2, IADR-0453 (#1411): 秘密情報・接続設定の管理。
//
// IADR-0135 決定 4: 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るため、モックは `apiRequest` に当てる。
//
// 🔴 **否定形（値の列が無い・一括の口が無い・権限外に画面が無い）は陽性対照と対で置く。**
// 何も描かない実装でも否定形だけなら緑になるためである。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc22SecretsRoute, sc22SecretsNav, sc22SecretsBreadcrumb } from '../index';

const ITEMS = [
  {
    item: 'llm-provider-credentials',
    vaultPath: 'msp/llm-provider-credentials',
    properties: ['anthropic-api-key', 'openai-api-key'],
    status: 'set',
    currentVersion: 3,
    lastUpdatedAt: '2026-09-14T01:02:03Z',
    lastUpdatedBy: 'sato.hanako',
  },
  {
    item: 'keycloak-smtp',
    vaultPath: 'msp/keycloak-smtp',
    properties: ['from', 'user', 'password'],
    status: 'notSet',
    currentVersion: null,
    lastUpdatedAt: null,
    lastUpdatedBy: null,
  },
  {
    item: 'wikijs-sync',
    vaultPath: 'msp/wikijs-sync',
    properties: ['apiKey'],
    status: 'unavailable',
    currentVersion: null,
    lastUpdatedAt: null,
    lastUpdatedBy: null,
  },
  {
    item: 'ast-app-secrets',
    vaultPath: 'ai-stock-trading/app-secrets',
    properties: ['finnhub-api-key'],
    status: 'set',
    currentVersion: 1,
    lastUpdatedAt: '2026-09-13T00:00:00Z',
    lastUpdatedBy: null,
  },
];

// 🔴 テスト用の明白なダミー値。本物の秘密は書かない。
const PLACEHOLDER = 'placeholder-value-for-sc22-ui-test';

function mockApi() {
  mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
    if (init?.method === 'PUT' && String(path).startsWith('/secrets/')) {
      return Promise.resolve(
        jsonResponse({
          item: 'llm-provider-credentials',
          property: 'openai-api-key',
          version: 4,
          updatedAt: '2026-09-14T02:00:00Z',
        }),
      );
    }
    if (String(path) === '/secrets') return Promise.resolve(jsonResponse(ITEMS));
    return Promise.resolve(jsonResponse([]));
  });
}

async function renderPage(roles: readonly string[] = ['platform-admin']) {
  return renderUnitRoute((shell) => [createSc22SecretsRoute(shell)], {
    initialEntry: '/admin/secrets',
    roles,
  });
}

const itemsTable = () => screen.findByRole('table', { name: '秘密情報の項目の一覧' });

async function openForm(user: ReturnType<typeof userEvent.setup>, itemName: string) {
  const rows = within(await itemsTable()).getAllByRole('row');
  const target = rows.find((row) => within(row).queryByText(itemName));
  await user.click(within(target!).getByRole('button', { name: '更新' }));
  return screen.getByTestId('secret-update-form');
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('SecretItemManagementPage (SC-22)', () => {
  // 05_screens §SC-22 主要素 1: 列は 項目名／用途／最終更新日時／最終更新者／操作。🔴 値の列を置かない。
  it('shows exactly the five planned columns and no value column', async () => {
    mockApi();
    await renderPage();

    expect(
      await screen.findByRole('heading', { name: '秘密情報・接続設定の管理', level: 1 }),
    ).toBeInTheDocument();
    const headers = within(await itemsTable())
      .getAllByRole('columnheader')
      .map((header) => header.textContent);
    expect(headers).toEqual(['項目名', '用途', '最終更新日時', '最終更新者', '操作']);
    expect(headers.some((header) => /値/.test(header ?? ''))).toBe(false);
    // 陽性対照: 行は 4 件描かれている（見出し行 ＋ 4）。
    expect(within(await itemsTable()).getAllByRole('row')).toHaveLength(5);
  });

  // 05_screens §SC-22 主要素 3: 未設定を明示し、「設定済みだが読み出せない」と区別する。
  it('renders not-set and unavailable distinctly from set items', async () => {
    mockApi();
    await renderPage();
    const table = within(await itemsTable());

    const rowOf = (name: string) =>
      table.getAllByRole('row').find((row) => within(row).queryByText(name))!;
    expect(within(rowOf('メール送信（SMTP）の認証情報')).getByText('未設定')).toBeInTheDocument();
    expect(within(rowOf('Wiki 同期の API キー')).getByText('取得できない')).toBeInTheDocument();
    expect(within(rowOf('外部 LLM の API キー')).getByText('設定済み')).toBeInTheDocument();
    // 最終更新者: 画面から書いた版だけに名前が付き、それ以外の設定済みは「記録なし」。
    expect(within(rowOf('外部 LLM の API キー')).getByText('sato.hanako')).toBeInTheDocument();
    expect(
      within(rowOf('株式自動売買の外部 API キーと通知')).getByText('記録なし'),
    ).toBeInTheDocument();
    // 「設定済み」の意味の限界を注記する（IADR-0453 決定 4）。
    expect(screen.getByTestId('secrets-status-note')).toHaveTextContent('判定できません');
  });

  // 05_screens §SC-22 主要素 2: 🔴 一括再投入のボタンを置かない（陽性対照: 行ごとの「更新」は 4 つ在る）。
  it('offers per-item update only and no bulk re-inject control', async () => {
    mockApi();
    await renderPage();
    await itemsTable();

    expect(screen.getAllByRole('button', { name: '更新' })).toHaveLength(4);
    expect(screen.queryByRole('button', { name: /一括|すべて|全項目|再投入/ })).toBeNull();
  });

  // 05_screens §SC-22 入力/バリデーション: マスクされた入力と、2 度目の入力が一致しないと送信できないこと。
  it('blocks submission until the confirmation matches, then writes one property with the reason', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    const form = within(await openForm(user, '外部 LLM の API キー'));

    // 書けるプロパティだけが選択肢に出る。
    expect(form.getAllByRole('option').map((option) => option.getAttribute('value'))).toEqual([
      'anthropic-api-key',
      'openai-api-key',
    ]);
    const value = form.getByLabelText('新しい値');
    const confirmation = form.getByLabelText('新しい値（確認のためもう一度）');
    expect(value).toHaveAttribute('type', 'password');
    expect(confirmation).toHaveAttribute('type', 'password');

    const submit = form.getByRole('button', { name: 'このプロパティを更新する' });
    expect(submit).toBeDisabled();

    await user.selectOptions(form.getByLabelText('更新するプロパティ'), 'openai-api-key');
    await user.type(value, PLACEHOLDER);
    // 🔴 **同じ長さで 1 文字だけ違う**確認を入れる（変異試験: 長さだけを比べる実装は、長さの違う入力では落ちない）。
    await user.type(confirmation, `${PLACEHOLDER.slice(0, -1)}X`);
    expect(form.getByTestId('secret-confirmation-mismatch')).toBeInTheDocument();
    expect(submit).toBeDisabled();
    await user.click(submit);
    expect(
      mocks.apiRequest.mock.calls.some(([, init]) => (init as RequestInit)?.method === 'PUT'),
    ).toBe(false);

    // 一致させると送れる（陽性対照）。
    await user.clear(confirmation);
    await user.type(confirmation, PLACEHOLDER);
    expect(form.queryByTestId('secret-confirmation-mismatch')).toBeNull();
    await user.type(form.getByLabelText('更新の理由（任意）'), '鍵の定期ローテーション');
    expect(submit).toBeEnabled();
    await user.click(submit);

    await waitFor(() => {
      const put = mocks.apiRequest.mock.calls.find(
        ([, init]) => (init as RequestInit)?.method === 'PUT',
      );
      expect(put).toBeDefined();
      expect(String(put![0])).toBe('/secrets/llm-provider-credentials');
      expect(JSON.parse(String((put![1] as RequestInit).body))).toEqual({
        property: 'openai-api-key',
        value: PLACEHOLDER,
        reason: '鍵の定期ローテーション',
      });
    });
    // 成功したら版を示し、送った値を入力欄に残さない。
    expect(await form.findByTestId('secret-update-done')).toHaveTextContent('版 4');
    expect(value).toHaveValue('');
    expect(confirmation).toHaveValue('');
  });

  // IADR-0453 決定 5: 保管先に届かない（503）ときは失敗を見せ、空の一覧に縮退しない。
  it('shows the vault failure instead of an empty list when the list returns 503', async () => {
    mocks.apiRequest.mockImplementation(() => Promise.reject(ApiError.fromStatus(503)));
    await renderPage();

    expect(await screen.findByText('秘密情報の項目を取得できませんでした。')).toBeInTheDocument();
    expect(
      screen.getByText(/保管先（Vault）が構成されていないか、接続できません/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: '秘密情報の項目の一覧' })).toBeNull();
  });

  // 05_screens §SC-22 アクセス制御: 運用者・システム管理者に限る。権限外には画面自体を表示しない。
  it('lets operators in', async () => {
    mockApi();
    await renderPage(['platform-operator']);

    expect(await itemsTable()).toBeInTheDocument();
  });

  it('hides the screen from other roles and never requests the list', async () => {
    mockApi();
    await renderPage(['platform-user']);

    expect(
      await screen.findByRole('heading', { name: '見つかりませんでした' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '秘密情報・接続設定の管理' })).toBeNull();
    expect(mocks.apiRequest).not.toHaveBeenCalled();
  });

  // 05_screens §SC-22「共通シェル: 左ナビ『運用』グループ」・権限外にはメニューを表示しない。
  it('declares the navigation entry in the ops group for admins and operators only', () => {
    expect(sc22SecretsNav).toMatchObject({
      to: '/admin/secrets',
      group: 'ops',
      requiresAnyRole: ['platform-admin', 'platform-operator'],
    });
    expect(sc22SecretsBreadcrumb).toMatchObject({ routePath: '/admin/secrets', group: 'ops' });
  });
});
