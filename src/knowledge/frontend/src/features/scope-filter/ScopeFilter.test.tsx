import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { I18nProvider } from '@lingui/react';
import { i18n } from '@foundation/i18n';
import { ScopeFilter } from './ScopeFilter';
import { EMPTY_SELECTION, type ScopeSelection } from './scopeFilter';

// 生成コードは mutator（`bffFetch`）→ `apiRequest` を通るのでモックは `apiRequest` に当てる。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

// FR-04, FR-05, SC-01, SC-08, #539: 対象範囲フィルタ（チップ）。
//
// **候補は権限内に限る**（`POST /bff/attribute-values`。ADR-0043 / [[IADR-0151]]）。
// 画面は候補へ何も足さない——存在秘匿の境界をサーバ側 1 か所に保つ。
function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** 軸ごとの候補を返すよう `apiRequest` を仕込む。 */
function respondWith(byAxis: Record<string, string[]>) {
  mocks.apiRequest.mockImplementation((_url: string, init?: RequestInit) => {
    const key = (JSON.parse(String(init?.body)) as { key: string }).key;
    return Promise.resolve(jsonResponse({ values: byAxis[key] ?? [] }));
  });
}

// `renderUnitRoute` と同じ既定にする——**本番の再試行（5xx・ネットワーク断を 1 回）をテストへ持ち込むと、
// 「1 軸だけ失敗したときの縮退」が再試行の待ち時間に隠れて観測できない**（実測: 縮退の表示が出ない）。
function testQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
}

function renderFilter(selection: ScopeSelection, onChange = vi.fn()) {
  render(
    <QueryClientProvider client={testQueryClient()}>
      <I18nProvider i18n={i18n}>
        <ScopeFilter selection={selection} onChange={onChange} />
      </I18nProvider>
    </QueryClientProvider>,
  );
  return onChange;
}

describe('ScopeFilter (SC-01 / SC-08)', () => {
  beforeEach(() => {
    mocks.apiRequest.mockReset();
  });

  // ★ 受け入れ基準 7: 候補 API の値でチップを組み立てる。
  it('renders chips from the permitted candidate values', async () => {
    respondWith({ tags: ['経理', '規程'], department: ['finance'], project: [] });

    renderFilter(EMPTY_SELECTION);

    expect(await screen.findByRole('button', { name: /経理/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /規程/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /finance/ })).toBeInTheDocument();
  });

  // **3 軸すべてを引く。** 引き漏らすと、その軸だけ黙って絞れなくなる。
  it('queries all three axes', async () => {
    respondWith({ tags: ['経理'], department: [], project: [] });

    renderFilter(EMPTY_SELECTION);
    await screen.findByRole('button', { name: /経理/ });

    const keys = mocks.apiRequest.mock.calls.map(
      (c) => (JSON.parse(String((c[1] as RequestInit).body)) as { key: string }).key,
    );
    expect(keys).toEqual(expect.arrayContaining(['tags', 'department', 'project']));
  });

  it('reports the selection when a chip is pressed', async () => {
    respondWith({ tags: ['経理'], department: [], project: [] });
    const onChange = renderFilter(EMPTY_SELECTION);

    await userEvent.click(await screen.findByRole('button', { name: /経理/ }));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ tags: ['経理'] }));
  });

  // INDEX 決定 21: **選択を色だけで表さない。** `aria-pressed` と ✓ の双方で示す。
  it('marks the selected chip without relying on colour alone', async () => {
    respondWith({ tags: ['経理', '規程'], department: [], project: [] });

    renderFilter({ ...EMPTY_SELECTION, tags: ['経理'] });

    const selected = await screen.findByRole('button', { name: /経理/ });
    expect(selected).toHaveAttribute('aria-pressed', 'true');
    expect(selected).toHaveTextContent('✓');

    const unselected = screen.getByRole('button', { name: /規程/ });
    expect(unselected).toHaveAttribute('aria-pressed', 'false');
    expect(unselected).not.toHaveTextContent('✓');
  });

  it('shows how many values are narrowing the scope', async () => {
    respondWith({ tags: ['経理', '規程'], department: [], project: [] });

    renderFilter({ ...EMPTY_SELECTION, tags: ['経理', '規程'] });

    expect(await screen.findByText('2 件で絞り込み中')).toBeInTheDocument();
  });

  it('says everything is in scope when nothing is selected', async () => {
    respondWith({ tags: ['経理'], department: [], project: [] });

    renderFilter(EMPTY_SELECTION);

    expect(await screen.findByText('すべてが対象')).toBeInTheDocument();
  });

  // **「候補が無い」と「権限が無い」を区別しない**（存在秘匿。IADR-0009）。
  it('uses a neutral message when there are no candidates at all', async () => {
    respondWith({ tags: [], department: [], project: [] });

    renderFilter(EMPTY_SELECTION);

    expect(await screen.findByText('絞り込める候補はありません。')).toBeInTheDocument();
  });

  // **1 軸の失敗で他の軸の候補まで失わない。** 対象範囲は任意の絞り込みであり、
  // 候補が引けないことは「検索・分析ができない」ことを意味しない。
  it('degrades one failing axis without losing the others', async () => {
    mocks.apiRequest.mockImplementation((_url: string, init?: RequestInit) => {
      const key = (JSON.parse(String(init?.body)) as { key: string }).key;
      if (key === 'department') return Promise.reject(new Error('boom'));
      return Promise.resolve(jsonResponse({ values: key === 'tags' ? ['経理'] : [] }));
    });

    renderFilter(EMPTY_SELECTION);

    expect(await screen.findByRole('button', { name: /経理/ })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText('候補を読み込み中…')).not.toBeInTheDocument());
  });
});
