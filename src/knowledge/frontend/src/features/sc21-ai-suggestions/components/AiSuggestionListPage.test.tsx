import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-21, UC-10, FR-18/FR-05 (#918): AI 提案一覧。
//
// IADR-0135 決定 4: 生成コードは mutator（bffFetch）→ **apiRequest** を通るため、モックは
// apiRequest に当てる（SC-18 と同じ作法）。
//
// 🔴 **否定形（一括承認が無い・承認ボタンが無い・画面が分かれていない）は陽性対照と対で置く。**
// 何も描かない実装でも否定形だけなら緑になるためである。変異試験の結果は作業仕様書に残す。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc21AiSuggestionsRoute } from '../routes/sc21AiSuggestionsRoute';

const DOC_A = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const DOC_B = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const DOC_C = 'cccccccc-cccc-cccc-cccc-cccccccccccc';
const TYPE_RELATED = '11111111-1111-1111-1111-111111111111';

const EDGE_TYPES = [{ id: TYPE_RELATED, name: 'related', layer: 'core', isSymmetric: true }];

const LINK_SUGGESTION = {
  id: '00000000-0000-0000-0000-000000000001',
  kind: 'link',
  sourceDocumentId: DOC_A,
  targetDocumentId: DOC_B,
  edgeTypeId: TYPE_RELATED,
  tagValue: null,
  rationale: '両文書が同じ規程を引いている',
  state: 'pending',
  rejectedCount: 0,
  reinstatedReason: null,
  sourceDocumentTitle: '経費精算規程 v3.2',
  targetDocumentTitle: '出張旅費精算ガイド',
};

const TAG_SUGGESTION = {
  id: '00000000-0000-0000-0000-000000000002',
  kind: 'tag',
  sourceDocumentId: DOC_C,
  targetDocumentId: null,
  edgeTypeId: null,
  tagValue: '精算',
  rationale: '本文に精算手続の記述が多い',
  state: 'pending',
  rejectedCount: 0,
  reinstatedReason: null,
  sourceDocumentTitle: '経理部 業務マニュアル',
  targetDocumentTitle: null,
};

const REINSTATED_SUGGESTION = {
  ...LINK_SUGGESTION,
  id: '00000000-0000-0000-0000-000000000003',
  rejectedCount: 1,
  reinstatedReason: 'source',
};

/** BFF の 2 面（提案の一覧 / 辺の型カタログ）へ応答を割り当てる。 */
function respond({
  suggestions = [LINK_SUGGESTION, TAG_SUGGESTION] as unknown,
  edgeTypes = EDGE_TYPES as unknown,
}: { suggestions?: unknown; edgeTypes?: unknown } = {}) {
  const reply = (value: unknown) =>
    value instanceof Error ? Promise.reject(value) : Promise.resolve(jsonResponse(value));
  // apiRequest が受けるパスは /bff 接頭辞を**除いた**形である（bffFetch が付け直す）。
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path.includes('/graph/suggestions')) return reply(suggestions);
    if (path.includes('/graph/edge-types')) return reply(edgeTypes);
    return reply([]);
  });
}

/** apiRequest が受けた一覧のパス（最後の 1 件）。 */
function lastListingPath(): string | undefined {
  const calls = mocks.apiRequest.mock.calls
    .map((c) => c[0] as string)
    .filter((p) => p.includes('/graph/suggestions'));
  return calls[calls.length - 1];
}

async function renderPage(initialEntry = '/ai-suggestions') {
  return renderUnitRoute((shell) => [createSc21AiSuggestionsRoute(shell)], { initialEntry });
}

/** 本文（tbody）の行。 */
const bodyRows = () => within(screen.getAllByRole('rowgroup')[1]).getAllByRole('row');

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('AiSuggestionListPage (SC-21)', () => {
  // A-01 / A-04 陽性対照: 4 列が揃い、**リンク提案とタグ提案が同じ 1 つの表に並ぶ**。
  it('renders one table holding both link and tag suggestions', async () => {
    respond();
    await renderPage();

    const table = await screen.findByRole('table', { name: 'AI 提案の一覧' });
    // 🔴 表が 1 つであること（種類ごとに表を割ると「画面を分けない」が形だけになる）。
    expect(screen.getAllByRole('table')).toHaveLength(1);
    expect(
      within(table)
        .getAllByRole('columnheader')
        .map((h) => h.textContent?.trim()),
    ).toEqual(['種類', '提案', '状態', '文書詳細']);

    const rows = bodyRows();
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent('リンク');
    expect(rows[0]).toHaveTextContent('経費精算規程 v3.2 → 出張旅費精算ガイド（related）');
    expect(rows[1]).toHaveTextContent('タグ');
    expect(rows[1]).toHaveTextContent('経理部 業務マニュアル に「精算」を付与');
  });

  // A-03: URL に state が無くても **pending で問い合わせる**（05_screens §SC-21 の既定）。
  it('queries pending by default', async () => {
    respond();
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(lastListingPath()).toContain('state=pending');
    expect(screen.getByLabelText('状態')).toHaveValue('pending');
  });

  // A-02: 状態フィルタは 4 値。**「すべて」は state=all として後段へ送る**。
  it('offers the four state options and forwards the chosen one', async () => {
    respond();
    await renderPage();

    const select = await screen.findByLabelText('状態');
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => (o as HTMLOptionElement).value),
    ).toEqual(['pending', 'approved', 'rejected', 'all']);

    await userEvent.selectOptions(select, 'all');
    await waitFor(() => expect(lastListingPath()).toContain('state=all'));

    await userEvent.selectOptions(select, 'rejected');
    await waitFor(() => expect(lastListingPath()).toContain('state=rejected'));
  });

  // A-04: 種類フィルタは**同じ一覧の絞り込み**である。選ぶと kind が後段へ渡る。
  it('forwards the kind filter and omits it for すべて', async () => {
    respond();
    await renderPage();

    const select = await screen.findByLabelText('種類');
    await userEvent.selectOptions(select, 'tag');
    await waitFor(() => expect(lastListingPath()).toContain('kind=tag'));
    // 表は 1 つのまま（種類を選んでも別画面へ行かない）。
    expect(screen.getAllByRole('table')).toHaveLength(1);

    await userEvent.selectOptions(select, 'all');
    // 陽性対照の対: 「すべて」では kind を送らない（後段の既定＝絞らない と一致させる）。
    await waitFor(() => expect(lastListingPath()).not.toContain('kind='));
  });

  // A-05: **全行**が SC-03（/docs/$id）への導線を持つ。タグ提案の行にも必ず在る。
  it('gives every row a link to the document detail screen', async () => {
    respond();
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    const rows = bodyRows();
    const hrefs = rows.map((row) =>
      within(row).getByRole('link', { name: '文書詳細で確認' }).getAttribute('href'),
    );
    expect(hrefs).toEqual([`/docs/${DOC_A}`, `/docs/${DOC_C}`]);
  });

  // 🔴 A-06 / A-07: **一括承認も、1 件ずつの承認・却下も、この画面には無い。**
  //
  // 陽性対照を先に置く —— 行が描かれ、操作できる要素（フィルタ・導線）が在ることを確かめてから
  // 否定形を測る。空の画面でも緑になる否定形にしない。
  it('offers no approval, rejection or bulk selection controls', async () => {
    respond();
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    // 陽性対照: 行は在り、操作できる要素も在る。
    expect(bodyRows()).toHaveLength(2);
    expect(screen.getAllByRole('link', { name: '文書詳細で確認' })).toHaveLength(2);
    expect(screen.getAllByRole('combobox')).toHaveLength(2);

    // 否定形 1: 承認・却下のボタンが無い（＝この画面から状態を変えられない）。
    expect(screen.queryByRole('button', { name: /承認/ })).toBeNull();
    expect(screen.queryByRole('button', { name: /却下/ })).toBeNull();
    // 否定形 2: 一括選択の足場（チェックボックス）が無い。
    expect(screen.queryAllByRole('checkbox')).toHaveLength(0);
    // 否定形 3: 「まとめて」の操作を示す語が画面に無い（説明文の否定形は除く）。
    expect(screen.queryByRole('button', { name: /一括|まとめて/ })).toBeNull();
  });

  // A-08: 再提示には**固定文言**を必ず添える（ADR-0033 決定 10）。
  it('shows the fixed reinstatement notice on a re-offered suggestion', async () => {
    respond({ suggestions: [REINSTATED_SUGGESTION] });
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(screen.getByTestId('reinstated-notice')).toHaveTextContent(
      'この提案は一度却下されましたが、文書が更新されたため再度提示しています。',
    );
  });

  // A-08 の対（陽性対照の裏）: 再提示でない提案には理由を出さない。
  it('does not show the reinstatement notice on a first-time suggestion', async () => {
    respond({ suggestions: [LINK_SUGGESTION] });
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(screen.queryByTestId('reinstated-notice')).toBeNull();
  });

  // A-09: 提案の根拠（なぜ関連と判断したか）を出す。
  it('shows the rationale for each suggestion', async () => {
    respond();
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(screen.getAllByTestId('rationale').map((n) => n.textContent)).toEqual([
      '両文書が同じ規程を引いている',
      '本文に精算手続の記述が多い',
    ]);
  });

  // A-11: 辺の型名は**辞書で解決する**（ADR-0033 決定 9）。改名すると一覧の表示も変わる。
  it('resolves the edge type name from the dictionary so renames are followed', async () => {
    respond({ edgeTypes: [{ ...EDGE_TYPES[0], name: '関連（改名後）' }] });
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(bodyRows()[0]).toHaveTextContent('（関連（改名後））');
  });

  // 状態は**色だけで意味を持たせない**（StatusBadge が文言を必須にする）。
  // 却下は「再提示を抑止中」であることまで示す（ADR-0033 決定 7）。
  it('labels the state in words, not by colour alone', async () => {
    respond({
      suggestions: [
        LINK_SUGGESTION,
        { ...TAG_SUGGESTION, state: 'approved' },
        { ...REINSTATED_SUGGESTION, state: 'rejected', reinstatedReason: null },
      ],
    });
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    const rows = bodyRows();
    expect(rows[0]).toHaveTextContent('承認待ち');
    expect(rows[1]).toHaveTextContent('承認済み');
    expect(rows[2]).toHaveTextContent('却下（再提示を抑止中）');
  });

  // A-12: 後段が引けないときに**空の一覧へ縮退しない**。
  it('does not degrade a backend failure into an empty listing', async () => {
    respond({ suggestions: new ApiError('server', 'boom', 500) });
    await renderPage();

    expect(await screen.findByTestId('suggestions-error')).toBeInTheDocument();
    expect(screen.queryByTestId('suggestions-empty')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  // 0 件は 0 件として描く（「引けない」とは別の状態である）。
  it('shows an empty state when there is nothing to triage', async () => {
    respond({ suggestions: [] });
    await renderPage();

    expect(await screen.findByTestId('suggestions-empty')).toBeInTheDocument();
    expect(screen.queryByTestId('suggestions-error')).toBeNull();
  });

  // 位置づけの固定文言: **なぜ一覧で承認できないのか**を必ず示す（05_screens §SC-21）。
  it('always explains that approval happens on the document detail screen', async () => {
    respond();
    await renderPage();

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    const help = screen.getByTestId('suggestions-help');
    expect(help).toHaveTextContent('承認・却下はこの画面では行いません');
    expect(help).toHaveTextContent('まとめて承認する操作は提供していません');
    expect(help).toHaveTextContent('閲覧権限のない文書に関する提案は、件数を含め表示されません');
  });

  // 🔴 A-04（機械的な固定）: **リンクとタグで画面（ルート）を分けていない。**
  // ルート表を走査する —— 画面を分ける退行は、描画の検査では捕まらない（別ルートは別テストになる）。
  it('registers exactly one route and no per-kind screens', async () => {
    respond();
    const { router } = await renderPage();

    const paths = Object.values(router.routesById)
      .map((r) => (r as { fullPath?: string }).fullPath ?? '')
      .filter((p) => p.includes('ai-suggestions'));

    // 陽性対照: ルートが取れている（0 件なら下の否定形は自明に成り立つ）。
    expect(paths).toContain('/ai-suggestions');
    expect(paths).toHaveLength(1);
  });

  // URL の未知の値は既定へ倒す（手打ちで画面を壊さない。値域の防壁はサーバに在る）。
  it('falls back to the defaults for unknown search parameters', async () => {
    respond();
    await renderPage('/ai-suggestions?state=maybe&kind=whatever');

    await screen.findByRole('table', { name: 'AI 提案の一覧' });
    expect(screen.getByLabelText('状態')).toHaveValue('pending');
    expect(screen.getByLabelText('種類')).toHaveValue('all');
    expect(lastListingPath()).toContain('state=pending');
  });
});
