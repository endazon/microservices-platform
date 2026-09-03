import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';

// SC-02, UC-01（代替フロー）, FR-03/FR-05: 検索結果一覧の再実装（#502）＋ 生成物への載せ替え（#519）。
// 検索語は URL（?q=）が単一情報源であり（IADR-0126 決定 3）、入力欄は取得の引き金にならない。
//
// IADR-0135 決定 2 / 決定 4（#519）: 検索は生成された操作関数（`bffSearch`）を `useQuery` に据える。
// 経路は mutator（`bffFetch`）→ **`apiRequest`** なので、モックは `apiRequest` に当てる。
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
}));

/** 送信された検索要求の本文（生成コードは JSON 文字列を `body` に載せる）。 */
function sentQuery(call: unknown[]): { query: string; topK: number } {
  const init = call[1] as RequestInit;
  return JSON.parse(String(init.body)) as { query: string; topK: number };
}

import { createSc02ResultsRoute } from '../routes/sc02ResultsRoute';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const RESPONSE = {
  results: [
    {
      chunkId: 'c1',
      documentId: DOC_ID,
      documentTitle: '経費精算規程 v3.2',
      text: '…精算の締め日は毎月25日とし…',
      score: 0.91,
      attributes: { confidentiality: 'internal' },
      tags: ['経理', '規程'],
      // SC-02（裁定 Q6 / #536）: 更新日時。索引（Qdrant のペイロード）由来である。
      updatedAt: '2026-07-24T03:00:00Z',
    },
  ],
  totalHits: 24,
  elapsedMs: 5,
};

// #536 / [[IADR-0149]] 決定 3: **本項目より前に索引されたチャンクは日時を持たない。**
// 再索引が済むまでの縮退を画面が描けることを固定するための応答。
const RESPONSE_NOT_REINDEXED = {
  ...RESPONSE,
  results: [{ ...RESPONSE.results[0], updatedAt: null }],
};

// #1193 / ADR-0070 決定 4 / [[IADR-0358]]: **本文を持たない文書**（テキスト層の無い PDF 相当）。
// 索引にはメタデータしか無く、`text` は空で `hasBody` が `false` で返る。
// **本文ありの行と 2 件並べる** —— 本文なしの表示が全件に付かないことを同じ描画で見るためである
// （陽性対照。issue の受け入れ基準 4）。
const BODYLESS_DOC_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const RESPONSE_WITH_BODYLESS = {
  ...RESPONSE,
  results: [
    RESPONSE.results[0],
    {
      chunkId: 'c2',
      documentId: BODYLESS_DOC_ID,
      documentTitle: 'スキャン版 就業規則',
      text: '',
      score: 0.42,
      attributes: { confidentiality: 'internal' },
      tags: ['人事'],
      updatedAt: '2026-07-20T03:00:00Z',
      hasBody: false,
    },
  ],
};

async function renderPage(initialPath = '/search') {
  return renderUnitRoute((shell) => [createSc02ResultsRoute(shell)], { initialEntry: initialPath });
}

async function search(term: string) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('キーワード・意味検索'), term);
  await user.click(screen.getByRole('button', { name: '検索' }));
  return user;
}

beforeEach(() => {
  mocks.apiRequest.mockReset();
});

afterEach(() => {
  act(() => {
    activate('ja');
  });
});

describe('SearchResultsPage (SC-02)', () => {
  // UC-01 代替フロー: キーワード検索のみで結果一覧を返す（AI 回答は呼ばない）。
  // UC-01 基本フロー 2 / FR-05: クライアントは ABAC スコープを送らない。
  it('searches via /bff/search and lists results linking to SC-03', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    await renderPage();
    await search('経費精算');

    const link = await screen.findByRole('link', { name: '経費精算規程 v3.2' });
    expect(link).toHaveAttribute('href', `/docs/${DOC_ID}`);
    expect(screen.getByText(/精算の締め日は毎月25日/)).toBeInTheDocument();
    expect(screen.getByText('経理')).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/search',
      expect.objectContaining({ method: 'POST' }),
    );
    expect(sentQuery(mocks.apiRequest.mock.calls[0])).toEqual({ query: '経費精算', topK: 20 });
    // AI 回答（/analysis/ask/stream）は呼ばない＝この画面は代替フローだけを担う。
    expect(mocks.apiRequest.mock.calls.every(([path]) => path === '/search')).toBe(true);
  });

  // FR-05: 一覧が全体ではないことを明示する（05_screens §SC-02「権限内のみ表示」）。
  // SC-02, FR-03, #536: 計画 §SC-02 主要素「結果テーブル（文書／タグ／**更新日時**）」。
  // 契約が裁定 Q6 を受けて `updatedAt` を持ったので列を出す（[[IADR-0149]]）。
  it('lists the updated-at column for each result', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    await renderPage();
    await search('経費');

    expect(await screen.findByRole('columnheader', { name: '更新日時' })).toBeInTheDocument();
    // ロケール依存の整形なので、日付そのものではなく「— ではない値が出ている」ことを見る。
    const row = screen.getByRole('link', { name: '経費精算規程 v3.2' }).closest('tr')!;
    expect(within(row).queryByText('—')).not.toBeInTheDocument();
  });

  // [[IADR-0149]] 決定 3: 未再索引のチャンクは `updatedAt` を持たない。**画面は `—` を描く**。
  // 「日時が無い」と「まだ再索引していない」を利用者へ区別して見せない（索引の内部事情である）。
  it('renders an em dash when the chunk has not been reindexed yet', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE_NOT_REINDEXED));
    await renderPage();
    await search('経費');

    const row = (await screen.findByRole('link', { name: '経費精算規程 v3.2' })).closest('tr')!;
    expect(within(row).getByText('—')).toBeInTheDocument();
  });

  // SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 6:
  // 本文を持たない文書は**結果から除外されず**、抜粋の位置へ「本文なし（原本を参照）」が出る。
  // **原本の所在を持つのは SC-03（文書詳細）**なので、その表示はそこへの導線になっている。
  it('shows a no-body notice linking to the original for body-less documents', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE_WITH_BODYLESS));
    await renderPage();
    await search('就業規則');

    // 除外されない（行として在る）。
    const row = (await screen.findByRole('link', { name: 'スキャン版 就業規則' })).closest('tr')!;
    // 抜粋の位置に「本文なし（原本を参照）」が出て、**原本（SC-03）へ辿れる**。
    const notice = within(row).getByRole('link', { name: '本文なし（原本を参照）' });
    expect(notice).toHaveAttribute('href', `/docs/${BODYLESS_DOC_ID}`);
  });

  // **陽性対照**: 本文ありの行は従来どおり抜粋が出て、本文なしの表示は付かない
  // （「全件に付く」実装では上のテストだけでは緑になる）。
  it('keeps the body excerpt for documents that have one', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE_WITH_BODYLESS));
    await renderPage();
    await search('就業規則');

    const row = (await screen.findByRole('link', { name: '経費精算規程 v3.2' })).closest('tr')!;
    expect(within(row).getByText(/精算の締め日は毎月25日/)).toBeInTheDocument();
    expect(within(row).queryByText('本文なし（原本を参照）')).not.toBeInTheDocument();
  });

  it('states that only permitted documents are listed', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    await renderPage();
    await search('経費精算');

    expect(await screen.findByText(/24 件（権限内のみ表示）/)).toBeInTheDocument();
    // 総数（24）> 表示件数（1）のときは、表示している件数も示す。
    expect(screen.getByText(/（表示 1 件）/)).toBeInTheDocument();
  });

  // IADR-0126 決定 3: `?q=` は検索語の単一情報源。ディープリンク・戻る操作でそのまま再現される。
  it('auto-searches from the ?q= deep link', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    await renderPage('/search?q=%E7%B5%8C%E8%B2%BB');

    expect(await screen.findByRole('link', { name: '経費精算規程 v3.2' })).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/search',
      expect.objectContaining({ method: 'POST' }),
    );
    expect(sentQuery(mocks.apiRequest.mock.calls[0])).toEqual({ query: '経費', topK: 20 });
  });

  // IADR-0126 決定 3: 発火点は URL の 1 つだけ。送信 1 回で要求は 1 回である
  // （旧実装は「送信の直接実行」と「?q= 変化の useEffect」で二重発火し、ガードで抑えていた）。
  it('fires exactly one request per submission (URL is the single source of truth)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    await renderPage();
    await search('経費精算');

    await screen.findByRole('link', { name: '経費精算規程 v3.2' });
    expect(mocks.apiRequest).toHaveBeenCalledTimes(1);
  });

  // IADR-0126 決定 3: URL が単一情報源であることは、**入力欄の表示にも及ぶ**。
  // 本画面が**アンマウントされずに `q` だけが変わる経路**（ブラウザの戻る／進む）を再現する。
  // 直すまでは、結果一覧だけが更新されて入力欄が古い語のまま残っていた（PR #505 レビュー指摘）。
  it('syncs the input box when only ?q= changes (browser back/forward, no remount)', async () => {
    mocks.apiRequest.mockImplementation((_path: string, init: RequestInit) => {
      const { query } = JSON.parse(String(init.body)) as { query: string };
      return Promise.resolve(
        jsonResponse({
          results: [{ ...RESPONSE.results[0], documentTitle: `${query} の文書` }],
          totalHits: 1,
          elapsedMs: 1,
        }),
      );
    });
    const { router } = await renderPage('/search?q=%E7%B5%8C%E8%B2%BB'); // 経費
    expect(await screen.findByRole('link', { name: '経費 の文書' })).toBeInTheDocument();
    expect(screen.getByLabelText('キーワード・意味検索')).toHaveValue('経費');

    // 画面を出したまま URL だけを進める（同一ルートのため再マウントされない）。
    await act(async () => {
      await router.navigate({ to: '/search', search: { q: '出張' } });
    });

    // 結果一覧が新しい語で更新され……
    expect(await screen.findByRole('link', { name: '出張 の文書' })).toBeInTheDocument();
    // ……**入力欄も追随する**（ここが崩れると、表示中の一覧と入力欄が食い違う）。
    expect(screen.getByLabelText('キーワード・意味検索')).toHaveValue('出張');

    // 戻る操作（履歴を 1 つ戻す）でも同じく追随する。
    await act(async () => {
      await router.history.back();
    });
    expect(await screen.findByRole('link', { name: '経費 の文書' })).toBeInTheDocument();
    expect(screen.getByLabelText('キーワード・意味検索')).toHaveValue('経費');
  });

  // 編集途中の値は、URL が外から変わった時点で捨てるのが正しい（利用者が別の検索語を選んだため）。
  it('discards the pending edit when ?q= changes from outside', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse(RESPONSE));
    const user = userEvent.setup();
    const { router } = await renderPage('/search?q=%E7%B5%8C%E8%B2%BB');
    await user.clear(screen.getByLabelText('キーワード・意味検索'));
    await user.type(screen.getByLabelText('キーワード・意味検索'), '書きかけ');

    await act(async () => {
      await router.navigate({ to: '/search', search: { q: '出張' } });
    });

    expect(screen.getByLabelText('キーワード・意味検索')).toHaveValue('出張');
  });

  // deny-by-default: 権限外・0 件はいずれも中立に表示する（存在秘匿・IADR-0009）。
  it('shows a neutral empty message when results are empty (existence hidden)', async () => {
    mocks.apiRequest.mockResolvedValue(jsonResponse({ results: [], totalHits: 0, elapsedMs: 1 }));
    await renderPage();
    await search('取締役会');

    expect(await screen.findByText('該当する文書が見つかりませんでした。')).toBeInTheDocument();
    // 「権限がない」を示唆する文言を出さない。
    expect(screen.queryByText(/権限がありません/)).not.toBeInTheDocument();
  });

  it('shows an alert when the search request fails', async () => {
    mocks.apiRequest.mockRejectedValue(
      new ApiError('server', 'サーバでエラーが発生しました。', 500),
    );
    await renderPage();
    await search('x');

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('サーバでエラーが発生しました'),
    );
  });

  // 空クエリでは要求を出さない（`enabled: false`）。
  it('does not request anything without a query', async () => {
    await renderPage();

    expect(mocks.apiRequest).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: '検索' })).toBeDisabled();
  });

  // 導線: SC-01 へ戻れる。
  it('links back to SC-01', async () => {
    await renderPage();
    expect(screen.getByRole('link', { name: '← チャットに戻る' })).toHaveAttribute('href', '/ask');
  });

  it('renders in English when the en locale is active', async () => {
    activate('en');
    await renderPage();

    expect(screen.getByRole('heading', { name: 'Search results' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Search' })).toBeInTheDocument();
  });
});
