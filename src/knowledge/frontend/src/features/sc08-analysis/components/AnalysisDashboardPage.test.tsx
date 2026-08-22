import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-08, UC-02, FR-07/FR-11/FR-05: AI分析ダッシュボードの再実装（#503）。
//
// 本画面は **orval 生成フック（useBffAnalysisAnalyze）** に載る（IADR-0127 決定 3）。
// 生成コードは mutator（bffFetch）→ apiRequest を通るため、モックは `apiRequest` に当てる
// ——`apiFetch` を差し替えても生成コードの経路には効かない。
//
// **［2026-08-06 追記］本コメントはかつて「useAnalysisAnalyze に載る唯一の画面」と書いていた。
// どちらも失効している。** フック名は `operationId` の規約統一で改名され（IADR-0135 決定 5 が
// IADR-0131 決定 3 を改定）、**当て先も本画面だけの作法ではなくなった**——#519 / IADR-0135 決定 4 で
// 画面テスト 13 ファイルすべてが `apiRequest` を差し替える形へ揃った。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

import { createSc08AnalysisRoute } from '../routes/sc08AnalysisRoute';

const DOC_ID = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee';

const ANSWER = {
  answer: '3部門に共通する失注要因は (1) 見積提示の遅延 …',
  citations: [{ chunkId: 'c1', documentId: DOC_ID, documentTitle: '第1営業部 Q1報告', score: 0.9 }],
  model: 'self-hosted',
  inputTokens: 120,
  outputTokens: 45,
};

async function renderPage() {
  return renderUnitRoute((shell) => [createSc08AnalysisRoute(shell)], { initialEntry: '/analyze' });
}

/** 指示を入力して分析を実行する。 */
async function analyze(instruction = 'Q1の営業報告を比較して') {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/分析内容（指示）/), instruction);
  await user.click(screen.getByRole('button', { name: '分析実行' }));
  return user;
}

// **［#539］呼び出しは URL で選ぶ。** 画面が対象範囲の候補（`/attribute-values`）を
// 起動時に引くようになったため、**`mock.calls[0]` は目的の呼び出しとは限らない**。
// 添字で取ると、候補の取得を足しただけでテストが壊れる（実際に壊れた）。
/** 指定した口への呼び出しだけを取り出す。 */
function callsTo(url: string) {
  return mocks.apiRequest.mock.calls.filter((c) => c[0] === url);
}

/** 送信された分析要求の本文を取り出す。 */
function sentBody(): unknown {
  const init = callsTo('/analysis/analyze')[0]?.[1] as RequestInit | undefined;
  return JSON.parse(String(init?.body));
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('AnalysisDashboardPage (SC-08)', () => {
  // ★ FR-04, SC-08, #539: 分析対象のチップ（タグ・部門・プロジェクト）を `range.attributeFilters` へ載せる。
  // **裁定 Q3「SC-08 の分析対象チップにも同じ裁定を適用する」の受け入れ基準である。**
  it('puts the selected scope chips into the data range', async () => {
    mocks.apiRequest.mockImplementation((url: string, init?: RequestInit) => {
      if (url === '/attribute-values') {
        const key = (JSON.parse(String(init?.body)) as { key: string }).key;
        return Promise.resolve(jsonResponse({ values: key === 'tags' ? ['経理'] : [] }));
      }
      return Promise.resolve(jsonResponse(ANSWER));
    });
    await renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /経理/ }));
    await analyze();

    expect(sentBody()).toMatchObject({ range: { attributeFilters: { tags: ['経理'] } } });
  });

  // **何も選ばなければ `attributeFilters` は載らない**（旧来の要求と同じ形になる）。
  it('omits the filters when no chip is selected', async () => {
    mocks.apiRequest.mockImplementation((url: string) =>
      Promise.resolve(jsonResponse(url === '/attribute-values' ? { values: [] } : ANSWER)),
    );
    await renderPage();
    await analyze();

    expect(sentBody()).not.toHaveProperty('range.attributeFilters');
  });

  // UC-02 基本 1・3・4: 分析対象と分析内容を指定し、結果と出典を得る。出典から SC-03 へ遷移する。
  it('runs an analysis and links the citations to SC-03', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(ANSWER));
    await renderPage();
    await analyze();

    expect(await screen.findByText(/3部門に共通する失注要因/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '第1営業部 Q1報告' })).toHaveAttribute(
      'href',
      `/docs/${DOC_ID}`,
    );
    expect(mocks.apiRequest).toHaveBeenCalledWith('/analysis/analyze', expect.anything());
  });

  // FR-07「分析・比較・抽出」。タスク種別と検索条件が要求へ載る。
  it('sends the task type and the search condition as the data range', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(ANSWER));
    const user = userEvent.setup();
    await renderPage();

    await user.type(screen.getByLabelText(/検索条件で追加/), 'Q1 営業報告');
    await user.selectOptions(screen.getByLabelText(/タスク種別/), 'Compare');
    await analyze('部門別に比較して');

    await waitFor(() =>
      expect(sentBody()).toEqual({
        instruction: '部門別に比較して',
        taskType: 'Compare',
        range: { query: 'Q1 営業報告' },
      }),
    );
  });

  // UC-02 例外フロー: 対象が権限外の場合は対象から除外し、**権限の有無を開示しない**（存在秘匿）。
  it.each([
    ['an empty answer (server-side degradation)', () => jsonResponse({ ...ANSWER, answer: '  ' })],
    ['a 403', () => Promise.reject(new ApiError('forbidden', '権限がありません。', 403))],
    ['a 404', () => Promise.reject(new ApiError('notFound', '見つかりませんでした。', 404))],
  ])('shows the same neutral message for %s', async (_name, make) => {
    mocks.apiRequest.mockImplementation(() => Promise.resolve(make()).then((r) => r));
    await renderPage();
    await analyze();

    expect(await screen.findByText('該当する情報が見つかりませんでした。')).toBeInTheDocument();
    // 「権限がありません」を示唆する文言を出さない。
    expect(screen.queryByText(/権限がありません/)).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 400 / 5xx / ネットワーク断は中立表示へ寄せない——寄せると「該当なし」と誤解して再試行しなくなる。
  it('reports a server error as an error, not as "nothing found"', async () => {
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();
    await analyze();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
    expect(screen.queryByText('該当する情報が見つかりませんでした。')).not.toBeInTheDocument();
  });

  // FR-11 / IADR-0111: model が空なのは「AI へ送信していない縮退」。空のまま出さない。
  it('says the AI was not used when the model is empty', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse({ ...ANSWER, model: '' }));
    await renderPage();
    await analyze();

    expect(await screen.findByText(/未使用（AI へ送信なし）/)).toBeInTheDocument();
  });

  it('shows the model and token counts when the AI was used', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(ANSWER));
    await renderPage();
    await analyze();

    expect(await screen.findByText(/モデル: self-hosted/)).toBeInTheDocument();
    expect(screen.getByText(/入力 120 ・出力 45 トークン/)).toBeInTheDocument();
  });

  it('does not submit an empty instruction', async () => {
    await renderPage();

    expect(screen.getByRole('button', { name: '分析実行' })).toBeDisabled();
    // **「1 度も通信していない」ではなく「分析を投げていない」を見る**（候補の取得は起動時に走る）。
    expect(callsTo('/analysis/analyze')).toHaveLength(0);
  });

  // 05_screens §SC-08 の注記（データ越境ポリシーと存在秘匿）。
  it('states the egress policy and the existence-hiding rule', async () => {
    await renderPage();

    expect(
      screen.getByText(/機密区分の高いデータは外部 API へ送信せずセルフホスト LLM で処理します/),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/権限外のデータは黙って対象外になります（存在秘匿）。/),
    ).toBeInTheDocument();
  });

  // **［2026-08-10 訂正 / #553］この試験はかつて
  // `does not render tag or folder chips (no contract for permitted candidates)` という名で、
  // 「権限内候補を返す API が無いため、チップの追加欄は置かない（planning#197 の裁定待ち）」
  // と書いて**チップが無いこと**を確かめていた。2 つの意味で誤っていた。**
  //
  //   1. **実装と食い違っていた** —— 裁定（2026-08-05 Q1・Q3・Q9）が着地し、候補の口は #540 で
  //      揃い、**チップは #539 で実装済み**である（同じファイルの先頭の試験が実際にそれを確かめている）。
  //   2. **そのうえ空振りだった** —— 照合していた `/タグ:/` `/フォルダ:/` は**コロン付き**で、
  //      軸ラベル（「タグ」「部門」「プロジェクト」）とは**どのみち一致しない**。
  //      したがってチップが描かれるようになっても、この試験は落ちなかった。
  //
  // いま確かめるのは**軸が 3 つであること**と、**「フォルダ」の軸が無いこと**である
  // ——「フォルダ」は保留ではなく**不採用**（裁定 Q9。ABAC 属性体系に `folder` が存在しない）。
  it('renders the three scope axes and offers no folder axis (Q9)', async () => {
    mocks.apiRequest.mockImplementation((url: string, init?: RequestInit) => {
      if (url === '/attribute-values') {
        const key = (JSON.parse(String(init?.body)) as { key: string }).key;
        return Promise.resolve(jsonResponse({ values: [`${key}-1`] }));
      }
      return Promise.resolve(jsonResponse(ANSWER));
    });
    await renderPage();

    // 「（権限内に限定）」は存在秘匿の説明そのものなので必ず出る。
    expect(screen.getByText('分析対象（権限内に限定）')).toBeInTheDocument();
    expect(screen.getByLabelText(/検索条件で追加/)).toBeInTheDocument();

    const scope = within(await screen.findByRole('region', { name: '対象範囲フィルタ' }));
    expect(scope.getByText('タグ')).toBeInTheDocument();
    expect(scope.getByText('部門')).toBeInTheDocument();
    expect(scope.getByText('プロジェクト')).toBeInTheDocument();
    expect(screen.queryByText('フォルダ')).not.toBeInTheDocument();
  });

  it('renders in English when the en locale is active', async () => {
    activate('en');
    await renderPage();

    expect(screen.getByRole('heading', { name: 'AI analysis request' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run analysis' })).toBeInTheDocument();
  });
});
