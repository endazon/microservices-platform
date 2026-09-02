import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';
import type { SseEvent } from '@foundation/api/apiClient';

// SC-01 → SC-02 → SC-03, UC-01: **利用者の主導線**（検索 → 結果一覧 → 文書詳細）を 1 本のルータで通す。
//
// 各画面の単体テストは画面ごとの挙動を見るが、導線は「ある画面が出すリンクの宛先が、別の画面が
// 実際に受け取れる形か」を見る。ここが割れていても画面単体のテストは全部通る（#502 が 3 画面を
// 1 本の issue にまとめた理由そのもの）。
//
// ［2026-09-02 / #1139］従前ここには「認証済みの導線を Playwright で実走できないため」と
// 書いていたが、**それは誤りである**（#1099 が実測で覆した。IADR-0330）。認証済みの画面は
// `platform/frontend/e2e/support/bffSession.ts` で実ブラウザ上に描かれ、3 画面それぞれの
// スモークが現に踏んでいる。**この層が在る理由は別**である ——
// ブラウザ E2E は画面ごとに開くため、**画面をまたぐリンクの宛先の突き合わせ**は見ない。
// 1 本のルータへ複数画面を載せて遷移そのものを測るのは、ここでしかできない。
//
// IADR-0135 決定 4（#519）: 検索・文書は生成物経由（→ `apiRequest`）、AI 回答は SSE のまま
// （`apiStream`。orval は SSE を扱えない。IADR-0131 決定 4）。**モックは両方に当てる。**
const mocks = vi.hoisted(() => ({ apiRequest: vi.fn(), apiStream: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiRequest: mocks.apiRequest,
  apiStream: mocks.apiStream,
}));
vi.mock('@foundation/config/runtimeConfig', () => ({
  appConfig: () => ({ wikiBaseUrl: undefined }),
}));

import { createSc01SearchRoute } from './sc01-search';
import { createSc02ResultsRoute } from './sc02-results';
import { createSc03DocumentRoute } from './sc03-document';

const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const TITLE = '経費精算規程 v3.2';

const SEARCH_RESPONSE = {
  results: [
    {
      chunkId: 'c1',
      documentId: DOC_ID,
      documentTitle: TITLE,
      text: '…精算の締め日は毎月25日とし…',
      score: 0.91,
      tags: ['経理'],
    },
  ],
  totalHits: 1,
  elapsedMs: 4,
};
const DETAIL = {
  id: DOC_ID,
  title: TITLE,
  status: 'published',
  markdownUri: 'storage://normalized/keihi.md',
  version: 3,
  attributes: { confidentiality: 'internal' },
  tags: ['経理'],
  createdAt: '2025-10-02T00:00:00Z',
  updatedAt: '2026-05-30T00:00:00Z',
};
const CONTENT = {
  id: DOC_ID,
  title: TITLE,
  markdown: '# 経費精算規程\n締め日は毎月25日。',
  sourceUri: null,
};

beforeEach(() => {
  mocks.apiRequest.mockReset();
  mocks.apiStream.mockReset();
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path === '/search') return Promise.resolve(jsonResponse(SEARCH_RESPONSE));
    if (path.endsWith('/content')) return Promise.resolve(jsonResponse(CONTENT));
    if (path.endsWith('/versions')) return Promise.resolve(jsonResponse([]));
    return Promise.resolve(jsonResponse(DETAIL));
  });
});

/** 3 画面を実アプリと同じ形（1 つの共通シェルの下）で束ねる。 */
async function renderFlow(initialEntry = '/ask') {
  return renderUnitRoute(
    (shell) => [
      createSc01SearchRoute(shell),
      createSc02ResultsRoute(shell),
      createSc03DocumentRoute(shell),
    ],
    { initialEntry },
  );
}

describe('search flow (SC-01 → SC-02 → SC-03)', () => {
  // UC-01 代替フロー（キーワード検索のみ）→ 基本フロー 5（出典＝文書へ到達）の一続き。
  it('walks from the question box to the result list and then to the document detail', async () => {
    const user = userEvent.setup();
    await renderFlow();

    // SC-01: 質問を入力し、AI 回答を省略して検索だけに進む。
    await user.type(screen.getByLabelText('質問・キーワード'), '経費精算');
    await user.click(screen.getByRole('link', { name: 'キーワード検索のみ →' }));

    // SC-02: `?q=` が引き継がれ、そのまま検索が走る（入力し直さない）。
    expect(await screen.findByRole('heading', { name: '検索結果一覧' })).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      '/search',
      expect.objectContaining({ method: 'POST' }),
    );
    // **［#539］呼び出しは URL で選ぶ**（SC-01 が起動時に候補を引くため添字は当てにならない）。
    const searchCall = mocks.apiRequest.mock.calls.find((c) => String(c[0]).startsWith('/search'));
    expect(JSON.parse(String((searchCall![1] as RequestInit).body))).toEqual({
      query: '経費精算',
      topK: 20,
    });

    // SC-03: 結果から文書詳細へ。
    await user.click(await screen.findByRole('link', { name: TITLE }));
    expect(await screen.findByRole('heading', { name: TITLE })).toBeInTheDocument();
    expect(screen.getByText(/締め日は毎月25日。/)).toBeInTheDocument();
    expect(mocks.apiRequest).toHaveBeenCalledWith(
      `/documents/${DOC_ID}`,
      expect.objectContaining({ method: 'GET' }),
    );
  });

  // UC-01 基本フロー 5: AI 回答の出典から直接 SC-03 へ到達できる（一覧を経由しない経路）。
  it('walks from an answer citation straight to the document detail', async () => {
    mocks.apiStream.mockImplementation(
      async (_path: string, _req: unknown, onEvent: (e: SseEvent) => void) => {
        onEvent({
          event: 'citations',
          data: JSON.stringify({
            citations: [
              {
                number: 1,
                documentId: DOC_ID,
                documentTitle: TITLE,
                chunkId: 'c1',
                sourceUri: null,
                score: 0.9,
                snippet: '締め日は毎月25日',
              },
            ],
          }),
        });
        onEvent({
          event: 'done',
          data: JSON.stringify({ answerId: 'x', model: 'm', inputTokens: 1, outputTokens: 1 }),
        });
      },
    );
    const user = userEvent.setup();
    await renderFlow();

    await user.type(screen.getByLabelText('質問・キーワード'), '締め日は？');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await user.click(await screen.findByRole('link', { name: TITLE }));
    expect(await screen.findByRole('heading', { name: TITLE })).toBeInTheDocument();
  });

  // UC-01 例外フロー（縮退運転）の導線: AI が使えないとき、検索結果一覧へ 1 クリックで到達する。
  it('reaches the result list from the degraded answer notice', async () => {
    mocks.apiStream.mockRejectedValue(new Error('llm down'));
    const user = userEvent.setup();
    await renderFlow();

    await user.type(screen.getByLabelText('質問・キーワード'), '経費精算');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await user.click(await screen.findByRole('link', { name: '検索結果一覧を開く →' }));
    expect(await screen.findByRole('link', { name: TITLE })).toBeInTheDocument();
  });
});
