import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-22, SC-06, 計画 ADR-0126 決定 1〜4, IADR-0501 (#458 段 S3): 群「データソースの資格情報」の画面。
//
// 🔴 **否定形（運用者に更新が無い・再起動の確認が無い・値が出ない）は陽性対照と対で置く。**
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc22SecretsRoute } from '../index';

const WIKI_ID = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const DB_ID = '6fa459ea-ee8a-3ca4-894e-db77e160355e';
const SAAS_ID = '1b4e28ba-2fa1-11d2-883f-0016d3cca427';

const member = (overrides: Record<string, unknown>) => ({
  memberId: WIKI_ID,
  displayName: '社内 Wiki',
  kind: 'wiki',
  vaultPath: `datasource/${WIKI_ID}`,
  propertyDetails: [{ name: 'apiToken', kind: 'value', sensitive: true }],
  status: 'notSet',
  currentVersion: null,
  lastUpdatedAt: null,
  lastUpdatedBy: null,
  supplySource: 'screen',
  ...overrides,
});

const MEMBERS = [
  member({}),
  member({
    memberId: DB_ID,
    displayName: '業務 DB',
    kind: 'db',
    vaultPath: `datasource/${DB_ID}`,
    propertyDetails: [{ name: 'password', kind: 'value', sensitive: true }],
    status: 'set',
    currentVersion: 2,
    lastUpdatedAt: '2026-10-05T01:02:03Z',
    lastUpdatedBy: 'sato.hanako',
  }),
  member({
    memberId: SAAS_ID,
    displayName: '勤怠 SaaS',
    kind: 'saas',
    vaultPath: `datasource/${SAAS_ID}`,
    supplySource: 'git',
  }),
];

// 🔴 テスト用の明白なダミー値。本物の秘密は書かない。
const PLACEHOLDER = 'placeholder-value-for-sc22-group-ui-test';

function mockApi(
  options: {
    writable?: boolean;
    writeSupply?: string;
    writeError?: ApiError;
    members?: readonly unknown[];
  } = {},
) {
  mocks.apiRequest.mockImplementation((path: string, init?: RequestInit) => {
    const p = String(path);
    if (init?.method === 'PUT' && p.startsWith('/secrets/groups/')) {
      if (options.writeError) return Promise.reject(options.writeError);
      return Promise.resolve(
        jsonResponse({
          group: 'datasource-credentials',
          memberId: WIKI_ID,
          property: 'apiToken',
          version: 1,
          updatedAt: '2026-10-06T00:00:00Z',
          supplySource: options.writeSupply ?? 'screen',
        }),
      );
    }
    if (p === '/secrets/groups/datasource-credentials')
      return Promise.resolve(
        jsonResponse({
          group: 'datasource-credentials',
          writable: options.writable ?? true,
          members: options.members ?? MEMBERS,
        }),
      );
    if (p === '/secrets') return Promise.resolve(jsonResponse([]));
    return Promise.resolve(jsonResponse([]));
  });
}

async function renderPage(roles: readonly string[] = ['platform-admin'], search = '') {
  return renderUnitRoute((shell) => [createSc22SecretsRoute(shell)], {
    initialEntry: `/admin/secrets${search}`,
    roles,
  });
}

const groupTable = () => screen.findByRole('table', { name: 'データソースの資格情報の一覧' });
const rowOf = async (id: string) =>
  within(await groupTable()).getByTestId(`datasource-credential-row-${id}`);
const puts = () =>
  mocks.apiRequest.mock.calls.filter(([, init]) => (init as RequestInit)?.method === 'PUT');

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

describe('DataSourceCredentialGroupPanel (SC-22 群)', () => {
  // ADR-0126 決定 1: 成員ごとに 1 行。列に値は無く、状態は語で描き分ける（未設定・設定済み）。
  it('lists one row per registered data source with status and no value column', async () => {
    mockApi();
    await renderPage();

    const headers = within(await groupTable())
      .getAllByRole('columnheader')
      .map((header) => header.textContent);
    expect(headers).toEqual(['項目名', '最終更新日時', '最終更新者', '供給元', '操作']);
    expect(within(await rowOf(WIKI_ID)).getByText('未設定')).toBeInTheDocument();
    expect(within(await rowOf(DB_ID)).getByText('設定済み')).toBeInTheDocument();
    expect(within(await rowOf(DB_ID)).getByText('sato.hanako')).toBeInTheDocument();
    expect(within(await rowOf(DB_ID)).getByText('業務DB')).toBeInTheDocument();
    expect(within(await rowOf(DB_ID)).getByText(`datasource/${DB_ID}`)).toBeInTheDocument();
  });

  // ADR-0126 決定 4: 供給元の 3 値。「画面」には「実行時に取得・次の同期から効く」を添える。
  it('shows the supply source with the runtime-fetch note for screen-supplied items', async () => {
    mockApi();
    await renderPage();

    const wiki = within(await rowOf(WIKI_ID));
    expect(wiki.getByText('画面')).toBeInTheDocument();
    expect(wiki.getByText('実行時に取得・次の同期から効く')).toBeInTheDocument();
    const saas = within(await rowOf(SAAS_ID));
    expect(saas.getByText('画面以外')).toBeInTheDocument();
    expect(saas.queryByText('実行時に取得・次の同期から効く')).toBeNull();
  });

  // 🔴 ADR-0126 決定 4「不明を 2 値へ寄せない」（#458 段 S2 の独立監査）: BFF が `unknown` を返したら
  // 「確認できない」と出し、「画面」にも「画面以外」にも寄せない。注記がその意味と次の手を伝える。
  it('shows unknown supply as cannot-confirm without folding it into screen or not-screen', async () => {
    mockApi({
      members: [member({ status: 'set', currentVersion: 1, supplySource: 'unknown' })],
    });
    await renderPage();

    const wiki = within(await rowOf(WIKI_ID));
    expect(wiki.getByText('確認できない')).toBeInTheDocument();
    expect(wiki.queryByText('画面')).toBeNull();
    expect(wiki.queryByText('画面以外')).toBeNull();
    expect(wiki.queryByText('実行時に取得・次の同期から効く')).toBeNull();
    expect(screen.getByTestId('datasource-credentials-supply-note')).toHaveTextContent(
      '「確認できない」の項目は',
    );
  });

  // 🔴 ADR-0126 決定 3: 運用者は閲覧だけ（`writable: false` なら「更新」も操作の列も無い）。陽性対照: 管理者には 3 つ。
  it('offers no update control to operators and explains why', async () => {
    mockApi({ writable: false });
    await renderPage(['platform-operator']);
    const table = within(await groupTable());

    expect(table.queryByRole('button', { name: '更新' })).toBeNull();
    expect(table.queryByRole('columnheader', { name: '操作' })).toBeNull();
    expect(screen.getByTestId('datasource-credentials-read-only')).toHaveTextContent('管理者だけ');
  });

  it('offers per-item update to admins', async () => {
    mockApi({ writable: true });
    await renderPage();

    expect(within(await groupTable()).getAllByRole('button', { name: '更新' })).toHaveLength(3);
    expect(screen.queryByTestId('datasource-credentials-read-only')).toBeNull();
  });

  // ADR-0126 決定 1・4: マスク入力＋確認入力 2 度。🔴 再起動の確認は出さず、押下で 1 プロパティだけを送る。
  it('writes one property after a matching confirmation without a restart confirmation', async () => {
    mockApi();
    const user = userEvent.setup();
    await renderPage();
    await user.click(within(await rowOf(WIKI_ID)).getByRole('button', { name: '更新' }));
    const form = within(screen.getByTestId('datasource-credential-update-form'));

    const value = form.getByLabelText('新しい値');
    const confirmation = form.getByLabelText('新しい値（確認のためもう一度）');
    expect(value).toHaveAttribute('type', 'password');
    expect(confirmation).toHaveAttribute('type', 'password');
    const submit = form.getByRole('button', { name: 'このプロパティを更新する' });

    await user.type(value, PLACEHOLDER);
    await user.type(confirmation, `${PLACEHOLDER.slice(0, -1)}X`);
    expect(form.getByTestId('datasource-credential-confirmation-mismatch')).toBeInTheDocument();
    expect(submit).toBeDisabled();

    await user.clear(confirmation);
    await user.type(confirmation, PLACEHOLDER);
    await user.type(form.getByLabelText('更新の理由（任意）'), '初回の投入');
    await user.click(submit);

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(screen.queryByRole('dialog')).toBeNull();
    const [path, init] = puts()[0];
    expect(path).toBe(`/secrets/groups/datasource-credentials/${WIKI_ID}`);
    expect(JSON.parse(String((init as RequestInit).body))).toEqual({
      property: 'apiToken',
      value: PLACEHOLDER,
      reason: '初回の投入',
    });
    const done = await form.findByTestId('datasource-credential-update-done');
    expect(done).toHaveTextContent('次の同期から使われます');
    // 🔴 送った値は画面に残さない。
    expect(value).toHaveValue('');
    expect(screen.queryByDisplayValue(PLACEHOLDER)).toBeNull();
  });

  // ADR-0126 決定 4: 「画面以外」の成員は書き込みを拒否せず、書いても使われないことを送る前と後に伝える。
  it('warns that a value written over plaintext will not be used', async () => {
    mockApi({ writeSupply: 'git' });
    const user = userEvent.setup();
    await renderPage();
    await user.click(within(await rowOf(SAAS_ID)).getByRole('button', { name: '更新' }));
    const form = within(screen.getByTestId('datasource-credential-update-form'));

    expect(form.getByTestId('datasource-credential-not-screen')).toBeInTheDocument();
    await user.type(form.getByLabelText('新しい値'), PLACEHOLDER);
    await user.type(form.getByLabelText('新しい値（確認のためもう一度）'), PLACEHOLDER);
    await user.click(form.getByRole('button', { name: 'このプロパティを更新する' }));

    expect(await form.findByTestId('datasource-credential-update-done')).toHaveTextContent(
      '使われません',
    );
  });

  // 書き込みの失敗は「値は保存されていない」を添えて出す（403・404）。
  it.each([
    [403, '管理者だけ'],
    [404, '登録されていないか、無効化されています'],
  ])('explains a %i failure and says the value was not saved', async (status, text) => {
    mockApi({ writeError: ApiError.fromStatus(status) });
    const user = userEvent.setup();
    await renderPage();
    await user.click(within(await rowOf(WIKI_ID)).getByRole('button', { name: '更新' }));
    const form = within(screen.getByTestId('datasource-credential-update-form'));
    await user.type(form.getByLabelText('新しい値'), PLACEHOLDER);
    await user.type(form.getByLabelText('新しい値（確認のためもう一度）'), PLACEHOLDER);
    await user.click(form.getByRole('button', { name: 'このプロパティを更新する' }));

    const error = await form.findByTestId('datasource-credential-update-error');
    expect(error).toHaveTextContent(text);
    expect(error).toHaveTextContent('値は保存されていません');
  });

  // SC-06 の導線（`?datasource=<ID>`）: 当該行を強調し、管理者には更新フォームを開く。
  it('focuses the linked data source and opens its form for admins', async () => {
    mockApi();
    await renderPage(['platform-admin'], `?datasource=${DB_ID}`);

    expect(await rowOf(DB_ID)).toHaveAttribute('aria-current', 'true');
    expect(await rowOf(WIKI_ID)).not.toHaveAttribute('aria-current');
    const form = await screen.findByTestId('datasource-credential-update-form');
    expect(form).toHaveTextContent('業務 DB');
  });

  // 運用者は強調だけで、フォームは開かない（書けない）。群に無い ID は注記で伝える。
  it('only highlights for operators and explains an id outside the group', async () => {
    mockApi({ writable: false });
    await renderPage(['platform-operator'], `?datasource=${DB_ID}`);
    expect(await rowOf(DB_ID)).toHaveAttribute('aria-current', 'true');
    expect(screen.queryByTestId('datasource-credential-update-form')).toBeNull();
  });

  it('notes when the linked data source is not in the group', async () => {
    mockApi();
    await renderPage(['platform-admin'], '?datasource=11111111-1111-1111-1111-111111111111');

    expect(await screen.findByTestId('datasource-credentials-focus-missing')).toBeInTheDocument();
    expect(screen.queryByTestId('datasource-credential-update-form')).toBeNull();
  });

  // 成員が取れない（502）ときは空の一覧に縮退しない。
  it('shows a failure instead of an empty group when the members cannot be read', async () => {
    mocks.apiRequest.mockImplementation((path: string) =>
      String(path) === '/secrets/groups/datasource-credentials'
        ? Promise.reject(ApiError.fromStatus(502))
        : Promise.resolve(jsonResponse([])),
    );
    await renderPage();

    expect(
      await screen.findByText('データソースの資格情報を取得できませんでした。'),
    ).toBeInTheDocument();
    expect(screen.getByText(/登録済みのデータソースの一覧を取得できません/)).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'データソースの資格情報の一覧' })).toBeNull();
  });
});
