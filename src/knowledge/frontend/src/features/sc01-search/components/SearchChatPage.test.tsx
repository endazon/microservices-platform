import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@foundation/api/ApiError';
import { activate } from '@foundation/i18n';
import { renderUnitRoute } from '@foundation/testing/renderUnitRoute';
import { jsonResponse } from '@foundation/testing/bffResponse';
import type { SseEvent } from '@foundation/api/apiClient';

// SC-01, UC-01, FR-03/FR-04/FR-05/FR-08: 主入口の再実装（#502）。
// UC-01 の基本フロー（入力 → ABAC → 検索 → 回答 → 出典）・代替フロー（キーワード検索のみ）・
// 例外フロー（LLM 不調時の縮退運転）を画面から観測できる形で固定する。
//
// IADR-0135 決定 1 / 決定 4（#519）: フィードバック送信だけを生成フックへ載せ替えた。
// 生成コードは mutator（`bffFetch`）→ **`apiRequest`** を通るのでモックは `apiRequest` に当てる。
// **本文の SSE は `apiStream` のまま**（orval は SSE を扱えない。IADR-0131 決定 4）。
const mocks = vi.hoisted(() => ({
  apiStream: vi.fn(),
  apiRequest: vi.fn(),
}));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiStream: mocks.apiStream,
  apiRequest: mocks.apiRequest,
}));

import { createSc01SearchRoute } from '../routes/sc01SearchRoute';

const ANSWER_ID = '11111111-1111-1111-1111-111111111111';
const DOC_ID = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const WIKI_DOC_ID = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

// #1200 / IADR-0365 決定 1: 出典が Wiki 由来かは**権限内の Wiki 台帳**（`GET /bff/wiki/pages`）で判定する。
const WIKI_PAGE = {
  id: 'page-b',
  documentId: WIKI_DOC_ID,
  title: '経費精算FAQ',
  slug: 'keihi-faq',
  wikiPath: `doc/${WIKI_DOC_ID}`,
  status: 'Active',
  syncedAt: '2026-09-01T00:00:00Z',
};

/** 画面が起動時・出典表示時に引く口の既定応答（台帳には Wiki 出典の文書だけが載る）。 */
function respondDefault(wikiPages: unknown = [WIKI_PAGE]) {
  mocks.apiRequest.mockImplementation((path: string) => {
    if (path === '/wiki/pages')
      return wikiPages instanceof Error
        ? Promise.reject(wikiPages)
        : Promise.resolve(jsonResponse(wikiPages));
    if (path === '/attribute-values') return Promise.resolve(jsonResponse({ values: [] }));
    return Promise.resolve(jsonResponse({}));
  });
}

const DOCUMENT_CITATION = {
  number: 1,
  documentId: DOC_ID,
  documentTitle: '経費精算規程 v3.2',
  chunkId: 'c1',
  sourceUri: 'storage://normalized/keihi.md',
  score: 0.91,
  snippet: '精算の締め日は毎月25日',
};
const WIKI_CITATION = {
  number: 2,
  documentId: WIKI_DOC_ID,
  documentTitle: 'Wiki: 経費精算FAQ',
  chunkId: 'c2',
  // `sourceUri` は判定に**使わない**（台帳で判定する）。形が Wiki らしくなくても台帳に載れば Wiki 出典である。
  sourceUri: 'storage://normalized/keihi-faq.md',
  score: 0.8,
  snippet: 'よくある質問',
};

/** SSE の一連（citations → token* → done）を流す `apiStream` の実装を仕込む。 */
function streamEvents(events: SseEvent[]) {
  mocks.apiStream.mockImplementation(
    async (_path: string, _req: unknown, onEvent: (e: SseEvent) => void) => {
      for (const ev of events) onEvent(ev);
    },
  );
}

const CITATIONS_EVENT: SseEvent = {
  event: 'citations',
  data: JSON.stringify({ citations: [DOCUMENT_CITATION, WIKI_CITATION] }),
};
const DONE_EVENT: SseEvent = {
  event: 'done',
  data: JSON.stringify({ answerId: ANSWER_ID, model: 'x', inputTokens: 1, outputTokens: 2 }),
};

async function renderPage() {
  return renderUnitRoute((shell) => [createSc01SearchRoute(shell)], { initialEntry: '/ask' });
}

async function ask(question: string) {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('質問・キーワード'), question);
  await user.click(screen.getByRole('button', { name: '送信' }));
  return user;
}

beforeEach(() => {
  mocks.apiStream.mockReset();
  mocks.apiRequest.mockReset();
  respondDefault();
});

afterEach(() => {
  // I18nProvider はロケール変更を購読して再描画する。act の外で切り替えると警告になる。
  act(() => {
    activate('ja');
  });
});

describe('SearchChatPage (SC-01)', () => {
  // ★ FR-04, SC-01, #539: 対象範囲フィルタで選んだ値を SSE の要求本文へ載せる。
  // **計画 §SC-01 の主要素「対象範囲フィルタ（タグ／部門／プロジェクト）」の受け入れ基準である。**
  it('sends the selected scope with the question', async () => {
    mocks.apiRequest.mockImplementation((url: string, init?: RequestInit) => {
      if (url === '/attribute-values') {
        const key = (JSON.parse(String(init?.body)) as { key: string }).key;
        return Promise.resolve(jsonResponse({ values: key === 'tags' ? ['経理'] : [] }));
      }
      return Promise.resolve(jsonResponse({}));
    });
    streamEvents([CITATIONS_EVENT, DONE_EVENT]);
    await renderPage();

    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /経理/ }));
    await user.type(screen.getByLabelText('質問・キーワード'), '締め日は？');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    const [, options] = mocks.apiStream.mock.calls[0] as [string, { json: unknown }];
    expect(options.json).toMatchObject({
      question: '締め日は？',
      attributeFilters: { tags: ['経理'] },
    });
  });

  // UC-01 基本フロー 1: 利用者が質問またはキーワードを入力する（空・空白のみは送信できない）。
  it('keeps submit disabled until a non-blank question is entered', async () => {
    const user = userEvent.setup();
    await renderPage();

    expect(screen.getByRole('button', { name: '送信' })).toBeDisabled();
    await user.type(screen.getByLabelText('質問・キーワード'), '   ');
    expect(screen.getByRole('button', { name: '送信' })).toBeDisabled();

    await user.type(screen.getByLabelText('質問・キーワード'), '経費');
    expect(screen.getByRole('button', { name: '送信' })).toBeEnabled();
  });

  // UC-01 基本フロー 2 / FR-05: 認可はサーバ側で解決する。クライアントは ABAC スコープを送らない。
  it('sends only the question (the client never sends an ABAC scope)', async () => {
    streamEvents([DONE_EVENT]);
    await renderPage();
    await ask('経費精算の締め日は？');

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalledTimes(1));
    const [path, req] = mocks.apiStream.mock.calls[0];
    expect(path).toBe('/analysis/ask/stream');
    expect(req).toEqual({ json: { question: '経費精算の締め日は？' } });
  });

  // UC-01 基本フロー 3-5: 回答を逐次表示し、出典を併記する。
  it('streams the answer tokens as they arrive and shows the sources', async () => {
    streamEvents([
      CITATIONS_EVENT,
      { event: 'token', data: JSON.stringify({ text: '締め日は' }) },
      { event: 'token', data: JSON.stringify({ text: '毎月25日です。' }) },
      DONE_EVENT,
    ]);
    await renderPage();
    await ask('締め日は？');

    expect(await screen.findByText('締め日は毎月25日です。')).toBeInTheDocument();
    expect(screen.getByText('出典（クリックで文書詳細／Wikiへ）')).toBeInTheDocument();
  });

  // UC-01 基本フロー 5: 文書の出典は SC-03（/docs/:id）へ内部遷移する。
  it('renders document citations linking to SC-03', async () => {
    streamEvents([CITATIONS_EVENT, DONE_EVENT]);
    await renderPage();
    await ask('締め日は？');

    const link = await screen.findByRole('link', { name: '経費精算規程 v3.2' });
    expect(link).toHaveAttribute('href', `/docs/${DOC_ID}`);
    // INDEX 決定 21: 色だけで意味を持たせない。種別はラベル（文字）でも示す。
    expect(screen.getAllByText('組織文書').length).toBe(2);
  });

  // UC-01 基本フロー 5 / UC-07: 台帳に載る出典は 📖 ＋ SC-04 の**文書別ディープリンク**へ送る（#1200）。
  it('renders wiki citations linking to the SC-04 deep link when the ledger lists the document', async () => {
    streamEvents([CITATIONS_EVENT, DONE_EVENT]);
    await renderPage();
    await ask('締め日は？');

    expect(await screen.findByRole('link', { name: 'Wiki: 経費精算FAQ' })).toHaveAttribute(
      'href',
      `/wiki?doc=${WIKI_DOC_ID}`,
    );
    // 台帳は出典が現れてから引く（問う前に Wiki の口を叩かない）。
    expect(mocks.apiRequest).toHaveBeenCalledWith('/wiki/pages', expect.anything());
    // ★ 陽性対照と対: 台帳に無い出典は同じ回答の中で 📄 のまま。
    expect(screen.getByRole('link', { name: '経費精算規程 v3.2' })).toHaveAttribute(
      'href',
      `/docs/${DOC_ID}`,
    );
  });

  // 台帳に無ければ Wiki 由来を推測しない（`sourceUri` の形も見ない）。
  it('treats a citation as a document when the ledger does not list it', async () => {
    respondDefault([]);
    streamEvents([CITATIONS_EVENT, DONE_EVENT]);
    await renderPage();
    await ask('締め日は？');

    expect(await screen.findByRole('link', { name: 'Wiki: 経費精算FAQ' })).toHaveAttribute(
      'href',
      `/docs/${WIKI_DOC_ID}`,
    );
  });

  // 台帳が読めなくても Wiki 由来を推測しない（到達できない導線へ送らない）。
  it('treats citations as documents when the ledger cannot be read', async () => {
    respondDefault(ApiError.fromStatus(502));
    streamEvents([CITATIONS_EVENT, DONE_EVENT]);
    await renderPage();
    await ask('締め日は？');

    expect(await screen.findByRole('link', { name: 'Wiki: 経費精算FAQ' })).toHaveAttribute(
      'href',
      `/docs/${WIKI_DOC_ID}`,
    );
  });

  // UC-01 代替フロー: キーワード検索のみで結果一覧を返し、AI 回答を省略する。
  it('offers a keyword-only search link carrying the current question', async () => {
    const user = userEvent.setup();
    await renderPage();
    await user.type(screen.getByLabelText('質問・キーワード'), ' 経費精算 ');

    expect(screen.getByRole('link', { name: 'キーワード検索のみ →' })).toHaveAttribute(
      'href',
      '/search?q=%E7%B5%8C%E8%B2%BB%E7%B2%BE%E7%AE%97',
    );
  });

  // UC-01 例外フロー: LLM が不調な場合は検索結果のみを返す（縮退運転）。
  it('degrades to keyword search when the answer stream reports an error event', async () => {
    streamEvents([{ event: 'error', data: JSON.stringify({ message: '分析に失敗しました。' }) }]);
    await renderPage();
    await ask('締め日は？');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('AI 回答を生成できませんでした');
    expect(screen.getByRole('link', { name: '検索結果一覧を開く →' })).toHaveAttribute(
      'href',
      '/search?q=%E7%B7%A0%E3%82%81%E6%97%A5%E3%81%AF%EF%BC%9F',
    );
  });

  // UC-01 例外フロー: 通信自体が失敗した場合も同じ縮退へ倒す。
  it('degrades to keyword search when the request itself fails', async () => {
    mocks.apiStream.mockRejectedValue(new Error('boom'));
    await renderPage();
    await ask('締め日は？');

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('AI 回答を生成できませんでした'),
    );
  });

  // IADR-0126 決定 1: 意図的な中断（連投・離脱）は失敗ではない。
  it('does not show an error when the stream is aborted', async () => {
    mocks.apiStream.mockRejectedValue(new DOMException('aborted', 'AbortError'));
    await renderPage();
    await ask('締め日は？');

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // FR-08: done で得た answerId を添えてフィードバックを送る。
  it('sends feedback with the answer id after the stream completes', async () => {
    streamEvents([DONE_EVENT]);
    mocks.apiRequest.mockResolvedValue(jsonResponse({}));
    await renderPage();
    const user = await ask('締め日は？');

    await user.click(await screen.findByRole('button', { name: '役に立った' }));
    await waitFor(() =>
      expect(mocks.apiRequest).toHaveBeenCalledWith(
        '/feedback',
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    // **［#539］呼び出しは URL で選ぶ**——画面が起動時に候補（`/attribute-values`）を引くため、
    // `mock.calls[0]` はフィードバックの呼び出しとは限らない。
    const feedbackCall = mocks.apiRequest.mock.calls.find((c) => c[0] === '/feedback');
    expect(JSON.parse(String((feedbackCall![1] as RequestInit).body))).toEqual({
      answerId: ANSWER_ID,
      rating: 'up',
      question: '締め日は？',
    });
    expect(screen.getByRole('button', { name: '役に立った' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(screen.getByText('フィードバックを送信しました。')).toBeInTheDocument();
  });

  // FR-08: 送信に失敗したら楽観的な押下状態を取り消す（押したのに何も起きない状態を残さない）。
  it('reverts the optimistic rating when the feedback request fails', async () => {
    streamEvents([DONE_EVENT]);
    mocks.apiRequest.mockRejectedValue(new Error('boom'));
    await renderPage();
    const user = await ask('締め日は？');

    await user.click(await screen.findByRole('button', { name: '役に立たなかった' }));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: '役に立たなかった' })).toHaveAttribute(
        'aria-pressed',
        'false',
      ),
    );
    expect(screen.getByRole('alert')).toHaveTextContent('フィードバックを送信できませんでした');
  });

  // IADR-0126 決定 1: 連投は前のストリームを中断し、本文・出典・回答 ID をリセットする。
  it('resets the previous answer when a new question is submitted', async () => {
    streamEvents([
      CITATIONS_EVENT,
      { event: 'token', data: JSON.stringify({ text: '古い回答' }) },
      DONE_EVENT,
    ]);
    await renderPage();
    const user = await ask('1回目');
    expect(await screen.findByText('古い回答')).toBeInTheDocument();

    // 2 回目は何も返さないストリームにして、前の本文が残らないことを見る。
    streamEvents([]);
    await user.clear(screen.getByLabelText('質問・キーワード'));
    await user.type(screen.getByLabelText('質問・キーワード'), '2回目');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => expect(screen.queryByText('古い回答')).not.toBeInTheDocument());
    expect(screen.queryByText('出典（クリックで文書詳細／Wikiへ）')).not.toBeInTheDocument();
  });

  // ADR-0031（i18n = Lingui〔ja / en〕）: 同じ画面がロケールで描き分けられる。
  it('renders in English when the en locale is active', async () => {
    activate('en');
    await renderPage();

    expect(
      screen.getByRole('heading', { name: 'Knowledge search / AI question' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
  });
});
