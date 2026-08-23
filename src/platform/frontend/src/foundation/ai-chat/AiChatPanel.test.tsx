import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from '@lingui/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { i18n } from '@foundation/i18n';
import type { SseEvent } from '@foundation/api/apiClient';

// 05_screens §共通シェル（右レール AI チャットパネル）/ IADR-0121 決定 5 / IADR-0131 決定 4:
// **SSE は `apiStream` を通る**。orval は SSE を生成できず、`EventSource` は Authorization を
// 付けられないため、この口が恒久的な正規の口である。モックはそこへ当てる。
const mocks = vi.hoisted(() => ({ apiStream: vi.fn() }));
vi.mock('@foundation/api/apiClient', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@foundation/api/apiClient')>()),
  apiStream: mocks.apiStream,
}));

import { AiChatPanel } from './AiChatPanel';
import { useAiChatStore } from './aiChatStore';

/** SSE の一連（token* → done）を流す `apiStream` の実装を仕込む。 */
function streamEvents(events: SseEvent[]) {
  mocks.apiStream.mockImplementation(
    async (_path: string, _req: unknown, onEvent: (e: SseEvent) => void) => {
      for (const ev of events) onEvent(ev);
    },
  );
}

const tokens = (...texts: string[]): SseEvent[] => [
  ...texts.map((text) => ({ event: 'token', data: JSON.stringify({ text }) })),
  { event: 'done', data: JSON.stringify({ answerId: 'a-1' }) },
];

/**
 * パネルだけをルータの下で描画する。
 *
 * 実アプリのルート木（`@foundation/routing/router`）を使わないのは、それが合成点（`@features`）＝
 * 他ユニットまで引き込むためである（`renderUnitRoute` と同じ理由）。パネルが router から読むのは
 * `location.pathname` だけなので、最小の木で足りる。
 */
async function renderPanel(initialEntry = '/ask') {
  const root = createRootRoute({ component: Outlet });
  const page = createRoute({ getParentRoute: () => root, path: '$', component: AiChatPanel });
  const router = createRouter({
    routeTree: root.addChildren([page]),
    history: createMemoryHistory({ initialEntries: [initialEntry] }),
  });
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const result = render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router as never} />
      </QueryClientProvider>
    </I18nProvider>,
  );
  await act(async () => {
    await router.load();
  });
  return { router, ...result };
}

beforeEach(() => {
  mocks.apiStream.mockReset();
  useAiChatStore.setState({ open: false, historyByScreen: {} });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('AiChatPanel', () => {
  // 05_screens §共通シェル: 既定は閉じている。閉じている間はランチャーだけを描く
  //（開いたままだと全画面の主領域が最初から狭くなる）。
  it('is closed by default and only renders the launcher', async () => {
    await renderPanel();
    expect(screen.getByRole('button', { name: 'AI チャットを開く' })).toBeInTheDocument();
    expect(
      screen.queryByRole('complementary', { name: 'AI チャットパネル' }),
    ).not.toBeInTheDocument();
  });

  // 05_screens §共通シェル: ランチャーで開閉できること。
  it('opens the rail from the launcher', async () => {
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
    expect(
      await screen.findByRole('complementary', { name: 'AI チャットパネル' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'AI チャットを閉じる' })).toBeInTheDocument();
  });

  // UC-01 / IADR-0121 決定 5: 途中経過は自前フックが持ち、`done` で 1 往復が履歴へ確定する。
  it('streams an answer and keeps the confirmed turn in the history', async () => {
    streamEvents(tokens('締め日は', '毎月25日です。'));
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));

    await user.type(screen.getByLabelText('質問'), '締め日は？');
    await user.click(screen.getByRole('button', { name: '送信' }));

    expect(await screen.findByText('締め日は毎月25日です。')).toBeInTheDocument();
    await waitFor(() => expect(useAiChatStore.getState().historyByScreen['/ask']).toHaveLength(1));
    expect(useAiChatStore.getState().historyByScreen['/ask'][0]).toMatchObject({
      question: '締め日は？',
      answer: '締め日は毎月25日です。',
      answerId: 'a-1',
    });
  });

  // FR-05: クライアントは ABAC スコープを送らない（送っても BFF は使わない＝権限昇格の防止）。
  it('sends only the question to the BFF stream endpoint', async () => {
    streamEvents(tokens('はい'));
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
    await user.type(screen.getByLabelText('質問'), 'q');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    const [path, req] = mocks.apiStream.mock.calls[0] as [string, { json: unknown }];
    expect(path).toBe('/analysis/ask/stream');
    expect(req.json).toEqual({ question: 'q' });
  });

  // UC-01 例外フロー: LLM が不調なときは縮退する。**色だけで意味を持たせない**（INDEX 決定 21）
  // ——`Alert` は tone ＋ アイコン ＋ ラベル必須の API であり、文言が読める形で残る。
  it('degrades with a labelled alert when the stream fails', async () => {
    mocks.apiStream.mockRejectedValue(new Error('boom'));
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
    await user.type(screen.getByLabelText('質問'), 'q');
    await user.click(screen.getByRole('button', { name: '送信' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('エラー');
    expect(alert).toHaveTextContent('回答を生成できませんでした。');
  });

  // #788: 意図的な中断（連投・パネルを閉じる）は失敗ではない——縮退表示を出さない。
  it('does not show an error when the stream is aborted', async () => {
    mocks.apiStream.mockRejectedValue(new DOMException('aborted', 'AbortError'));
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
    await user.type(screen.getByLabelText('質問'), 'q');
    await user.click(screen.getByRole('button', { name: '送信' }));

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 05_screens §共通シェル「画面別履歴」: 別の画面で開くと、その画面の履歴だけが見える。
  it('shows only the history of the current screen', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: {
        '/ask': [{ id: '1', question: 'ask-q', answer: 'ask-a', answerId: null }],
        '/analyze': [{ id: '2', question: 'analyze-q', answer: 'analyze-a', answerId: null }],
      },
    });
    await renderPanel('/analyze');
    expect(await screen.findByText('analyze-q')).toBeInTheDocument();
    expect(screen.queryByText('ask-q')).not.toBeInTheDocument();
  });

  // 05_screens §共通シェル「画面ごとの保持／全消去」: 1 画面ぶんの消去は他画面を巻き添えにしない。
  it('clears this screen without touching the others', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: {
        '/ask': [{ id: '1', question: 'ask-q', answer: 'ask-a', answerId: null }],
        '/analyze': [{ id: '2', question: 'analyze-q', answer: 'analyze-a', answerId: null }],
      },
    });
    const user = userEvent.setup();
    await renderPanel('/analyze');
    await user.click(await screen.findByRole('button', { name: 'この画面の履歴を消去' }));

    await waitFor(() =>
      expect(useAiChatStore.getState().historyByScreen['/analyze']).toBeUndefined(),
    );
    expect(useAiChatStore.getState().historyByScreen['/ask']).toHaveLength(1);
  });

  // 05_screens §共通シェル「全消去」。
  it('clears every screen', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: {
        '/ask': [{ id: '1', question: 'ask-q', answer: 'ask-a', answerId: null }],
        '/analyze': [{ id: '2', question: 'analyze-q', answer: 'analyze-a', answerId: null }],
      },
    });
    const user = userEvent.setup();
    await renderPanel('/analyze');
    await user.click(await screen.findByRole('button', { name: '全消去' }));
    await waitFor(() => expect(useAiChatStore.getState().historyByScreen).toEqual({}));
  });
});
