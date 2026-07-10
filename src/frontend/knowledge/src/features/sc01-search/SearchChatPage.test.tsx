import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { SseEvent } from '@foundation/api/apiClient';

// SC-01, FR-03/FR-04/FR-08: SSE 逐次表示・出典併記・👍/👎・横断検索を検証する。
const mocks = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  apiStream: vi.fn(),
}));
vi.mock('@foundation/api/apiClient', () => ({ apiFetch: mocks.apiFetch, apiStream: mocks.apiStream }));

import { SearchChatPage } from './SearchChatPage';

const CITATION = {
  number: 1,
  documentId: 'd1',
  documentTitle: '経費規程 2025',
  chunkId: 'c1',
  sourceUri: 'https://wiki.example/expense',
  score: 0.9,
  snippet: '第3条',
};
const SEARCH = {
  results: [{ chunkId: 'c1', documentId: 'd1', documentTitle: '経費規程 2025', text: '第3条 …', score: 0.9, markdownUri: 'https://doc.example/e.md' }],
  totalHits: 1,
  elapsedMs: 5,
};

// scripted stream: citations → token(回答) → token(です) → done
function scriptStream(events: SseEvent[]) {
  mocks.apiStream.mockImplementation(
    async (_path: string, _req: unknown, onEvent: (e: SseEvent) => void) => {
      for (const e of events) onEvent(e);
    },
  );
}

const HAPPY_EVENTS: SseEvent[] = [
  { event: 'citations', data: JSON.stringify({ citations: [CITATION] }) },
  { event: 'token', data: JSON.stringify({ text: '回答' }) },
  { event: 'token', data: JSON.stringify({ text: 'です' }) },
  { event: 'done', data: JSON.stringify({ answerId: 'aid-1', model: 'm', inputTokens: 1, outputTokens: 2 }) },
];

beforeEach(() => {
  mocks.apiFetch.mockReset();
  mocks.apiStream.mockReset();
  mocks.apiFetch.mockImplementation(async (path: string) =>
    path.startsWith('/search') ? SEARCH : {},
  );
});

describe('SearchChatPage (SC-01)', () => {
  it('streams the answer, shows citations, and cross-search results', async () => {
    scriptStream(HAPPY_EVENTS);
    render(<SearchChatPage />);

    await userEvent.type(screen.getByLabelText('質問・キーワード'), '経費規程は？');
    await userEvent.click(screen.getByRole('button', { name: '質問する' }));

    // 本文が連結表示される。
    expect(await screen.findByText('回答です')).toBeInTheDocument();
    // 出典（Wiki=SC-04 等へのリンク）は AI回答 内に表示される。
    const answer = screen.getByRole('article', { name: 'AI回答' });
    expect(within(answer).getByRole('link', { name: '経費規程 2025' })).toHaveAttribute(
      'href',
      'https://wiki.example/expense',
    );
    // 横断検索結果（検索結果リンクは markdownUri）。
    const searchSection = screen.getByRole('region', { name: '検索結果' });
    expect(within(searchSection).getByRole('link', { name: '経費規程 2025' })).toHaveAttribute(
      'href',
      'https://doc.example/e.md',
    );
  });

  it('sends 👍 feedback with the answerId after done', async () => {
    scriptStream(HAPPY_EVENTS);
    render(<SearchChatPage />);

    await userEvent.type(screen.getByLabelText('質問・キーワード'), 'x');
    await userEvent.click(screen.getByRole('button', { name: '質問する' }));
    await screen.findByText('回答です');

    await userEvent.click(screen.getByRole('button', { name: '👍' }));

    const feedbackCall = mocks.apiFetch.mock.calls.find((c) => c[0] === '/feedback');
    expect(feedbackCall).toBeTruthy();
    expect(feedbackCall![1].json).toMatchObject({ answerId: 'aid-1', rating: 'up' });
    expect(await screen.findByText(/フィードバックを送信しました/)).toBeInTheDocument();
  });

  it('shows an alert when the stream emits an error event', async () => {
    scriptStream([{ event: 'error', data: '{}' }]);
    render(<SearchChatPage />);

    await userEvent.type(screen.getByLabelText('質問・キーワード'), 'x');
    await userEvent.click(screen.getByRole('button', { name: '質問する' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('回答の生成に失敗');
  });

  it('disables submit while the question is empty', () => {
    render(<SearchChatPage />);
    expect(screen.getByRole('button', { name: '質問する' })).toBeDisabled();
  });
});
