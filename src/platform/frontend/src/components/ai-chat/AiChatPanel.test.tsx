import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
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
import { Notifications } from '@foundation/ui/notifications';

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
import type { AiChatTurn } from './aiChatStore';

/** SSE の一連（token* → done）を流す `apiStream` の実装を仕込む。 */
function streamEvents(events: SseEvent[]) {
  mocks.apiStream.mockImplementation(
    async (_path: string, _req: unknown, onEvent: (e: SseEvent) => void) => {
      for (const ev of events) onEvent(ev);
    },
  );
}

/**
 * 途中で止められるストリーム: 最初のイベント列を流したあと、`signal` が abort されるまで待つ
 * （実際の `apiStream` は `reader.read()` が AbortError で reject する）。
 */
function streamThenHang(events: SseEvent[]) {
  mocks.apiStream.mockImplementation(
    (_path: string, _req: unknown, onEvent: (e: SseEvent) => void, signal?: AbortSignal) =>
      new Promise<void>((_resolve, reject) => {
        for (const ev of events) onEvent(ev);
        signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      }),
  );
}

const tokens = (...texts: string[]): SseEvent[] => [
  ...texts.map((text) => ({ event: 'token', data: JSON.stringify({ text }) })),
  { event: 'done', data: JSON.stringify({ answerId: 'a-1' }) },
];

const CITATIONS_EVENT: SseEvent = {
  event: 'citations',
  data: JSON.stringify({
    citations: [
      {
        number: 1,
        documentId: 'd-1',
        documentTitle: '経費精算規程 v3.2',
        chunkId: 'c1',
        score: 0.9,
        snippet: '締め日は毎月25日',
      },
    ],
  }),
};

const turn = (id: string, question: string, answer: string): AiChatTurn => ({
  id,
  question,
  answer,
  answerId: null,
  citations: [],
  stopped: false,
});

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
        <Notifications />
      </QueryClientProvider>
    </I18nProvider>,
  );
  await act(async () => {
    await router.load();
  });
  return { router, ...result };
}

async function openAndAsk(question: string) {
  const user = userEvent.setup();
  await renderPanel();
  await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
  // #1437 / IADR-0443: レール本体は `React.lazy` で読み込む。**同期取得にしない**
  // （遅延 import の解決を待たずに引くと、遅延化した瞬間に全テストが落ちる）。
  await user.type(await screen.findByLabelText('質問'), question);
  await user.click(screen.getByRole('button', { name: '送信' }));
  return user;
}

beforeEach(() => {
  mocks.apiStream.mockReset();
  useAiChatStore.setState({ open: false, historyByScreen: {} });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('AiChatPanel', () => {
  // 05_screens §共通シェル: 既定は閉じている。閉じている間は列にランチャーだけを描く
  //（開いたままだと全画面の主領域が最初から狭くなる。列は残す——開閉で本文の幅を揺らさない）。
  it('is closed by default and only renders the launcher', async () => {
    await renderPanel();
    expect(screen.getByRole('button', { name: 'AI チャットを開く' })).toBeInTheDocument();
    expect(
      screen.queryByRole('complementary', { name: 'AI チャットパネル' }),
    ).not.toBeInTheDocument();
  });

  // 05_screens §共通シェル: ランチャーで開閉できること。モック `.rr-h` の見出しと `.rr-hist` の件数。
  it('opens the rail from the launcher and closes it from the header', async () => {
    const user = userEvent.setup();
    await renderPanel();
    await user.click(screen.getByRole('button', { name: 'AI チャットを開く' }));
    const rail = await screen.findByRole('complementary', { name: 'AI チャットパネル' });
    expect(within(rail).getByRole('heading', { name: 'AIチャット' })).toBeInTheDocument();
    expect(within(rail).getByText('履歴（/ask） 0件')).toBeInTheDocument();
    // 契約に無い機能（画面コンテキストの添付・履歴の復元）は書かない。在ることだけを書く。
    expect(within(rail).getByText('履歴は画面単位。リロードで消えます')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'AI チャットを閉じる' }));
    expect(
      screen.queryByRole('complementary', { name: 'AI チャットパネル' }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'AI チャットを開く' })).toBeInTheDocument();
  });

  // UC-01 / IADR-0121 決定 5: 途中経過は自前フックが持ち、`done` で 1 往復が履歴へ確定する。
  // 裁定 6: 本文は Markdown として描く。
  it('streams an answer, renders it as markdown and keeps the confirmed turn in the history', async () => {
    streamEvents(tokens('締め日は', '**毎月25日**です。'));
    await openAndAsk('締め日は？');

    expect(await screen.findByText('毎月25日', { selector: 'strong' })).toBeInTheDocument();
    await waitFor(() => expect(useAiChatStore.getState().historyByScreen['/ask']).toHaveLength(1));
    expect(useAiChatStore.getState().historyByScreen['/ask'][0]).toMatchObject({
      question: '締め日は？',
      answer: '締め日は**毎月25日**です。',
      answerId: 'a-1',
      stopped: false,
    });
    expect(screen.getByText('履歴（/ask） 1件')).toBeInTheDocument();
  });

  // A-8: `citations` イベントを購読し、回答の下に出典（アイコン ＋ ラベル）を描く。本文の [1] は脚注へ結ぶ。
  it('subscribes to citations and lists them under the answer with a footnote link', async () => {
    streamEvents([CITATIONS_EVENT, ...tokens('締め日は毎月25日です[1]')]);
    await openAndAsk('締め日は？');

    await waitFor(() => expect(useAiChatStore.getState().historyByScreen['/ask']).toHaveLength(1));
    expect(useAiChatStore.getState().historyByScreen['/ask'][0].citations).toHaveLength(1);
    expect(await screen.findByText('経費精算規程 v3.2')).toBeInTheDocument();
    expect(screen.getByText('組織文書')).toBeInTheDocument();
    const footnote = await screen.findByRole('link', { name: '[1]' });
    const target = footnote.getAttribute('href')!.slice(1);
    expect(document.getElementById(target)).toHaveTextContent('経費精算規程 v3.2');
  });

  // FR-05: クライアントは ABAC スコープを送らない（送っても BFF は使わない＝権限昇格の防止）。
  it('sends only the question to the BFF stream endpoint', async () => {
    streamEvents(tokens('はい'));
    await openAndAsk('q');

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    const [path, req] = mocks.apiStream.mock.calls[0] as [string, { json: unknown }];
    expect(path).toBe('/analysis/ask/stream');
    expect(req.json).toEqual({ question: 'q' });
  });

  // 第 4 弾「体験の穴」(3) 停止: 進行中は「停止」で中断し、そこまでの部分回答を「（停止）」付きで履歴に残す。
  it('stops the stream and keeps the partial answer in the history marked as stopped', async () => {
    streamThenHang([{ event: 'token', data: JSON.stringify({ text: '締め日は' }) }]);
    const user = await openAndAsk('締め日は？');

    expect(await screen.findByRole('button', { name: '停止' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '停止' }));

    await waitFor(() => expect(useAiChatStore.getState().historyByScreen['/ask']).toHaveLength(1));
    expect(useAiChatStore.getState().historyByScreen['/ask'][0]).toMatchObject({
      answer: '締め日は',
      stopped: true,
      answerId: null,
    });
    expect(screen.getByText('（停止）')).toBeInTheDocument();
    // 中断は失敗ではない。縮退表示を出さない。
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '停止' })).not.toBeInTheDocument();
  });

  // 第 4 弾「体験の穴」(3) 再生成: 直前の質問を同じ内容で再送する。
  it('regenerates by re-sending the previous question', async () => {
    streamEvents(tokens('一回目'));
    const user = await openAndAsk('締め日は？');
    expect(await screen.findByText('一回目')).toBeInTheDocument();

    streamEvents(tokens('二回目'));
    await user.click(screen.getByRole('button', { name: '再生成' }));
    expect(await screen.findByText('二回目')).toBeInTheDocument();
    expect(mocks.apiStream).toHaveBeenCalledTimes(2);
    const [, req] = mocks.apiStream.mock.calls[1] as [string, { json: unknown }];
    expect(req.json).toEqual({ question: '締め日は？' });
  });

  // UC-01 例外フロー: LLM が不調なときは縮退する。**色だけで意味を持たせない**（INDEX 決定 21）
  // ——`Alert` は tone ＋ アイコン ＋ ラベル必須の API であり、文言が読める形で残る。再生成で復帰できる。
  it('degrades with a labelled alert when the stream fails and offers to regenerate', async () => {
    mocks.apiStream.mockRejectedValue(new Error('boom'));
    await openAndAsk('q');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('エラー');
    expect(alert).toHaveTextContent('回答を生成できませんでした。');
    expect(screen.getByRole('button', { name: '再生成' })).toBeInTheDocument();
  });

  // #788: 意図的な中断（連投・パネルを閉じる）は失敗ではない——縮退表示を出さない。
  it('does not show an error when the stream is aborted', async () => {
    mocks.apiStream.mockRejectedValue(new DOMException('aborted', 'AbortError'));
    await openAndAsk('q');

    await waitFor(() => expect(mocks.apiStream).toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  // 裁定 6（コピー）: 回答本文をクリップボードへ写し、結果を通知（アイコン ＋ ラベル ＋ 本文）で伝える。
  it('copies the answer to the clipboard and notifies', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: { '/ask': [turn('1', 'q', '締め日は **25日**')] },
    });
    const user = userEvent.setup();
    await renderPanel();
    const writeText = vi.spyOn(navigator.clipboard, 'writeText');

    await user.click(await screen.findByRole('button', { name: '回答をコピー' }));
    // 写すのは Markdown の原文（描画後の HTML ではない）。
    expect(writeText).toHaveBeenCalledWith('締め日は **25日**');
    expect(await screen.findByText('コピーしました')).toBeInTheDocument();
  });

  // 第 4 弾「体験の穴」(3) 追従スクロール: 上へ遡ると「最新へ」が出て、押すと下端へ戻る。
  it('offers a "latest" button when the user scrolls up and hides it once back at the bottom', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: { '/ask': [turn('1', 'q', 'a')] },
    });
    const user = userEvent.setup();
    await renderPanel();
    const list = await screen.findByRole('list', { name: '会話履歴' });
    // jsdom はレイアウトを持たないので、容器の寸法を模擬する。
    let scrollTop = 500;
    Object.defineProperty(list, 'clientHeight', { get: () => 100 });
    Object.defineProperty(list, 'scrollHeight', { get: () => 600 });
    Object.defineProperty(list, 'scrollTop', {
      get: () => scrollTop,
      set: (v: number) => {
        scrollTop = v;
      },
    });
    expect(screen.queryByRole('button', { name: '最新へ' })).not.toBeInTheDocument();

    scrollTop = 0;
    act(() => {
      list.dispatchEvent(new Event('scroll'));
    });
    await user.click(await screen.findByRole('button', { name: '最新へ' }));
    expect(scrollTop).toBe(600);
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: '最新へ' })).not.toBeInTheDocument(),
    );
  });

  // 05_screens §共通シェル「画面別履歴」: 別の画面で開くと、その画面の履歴だけが見える。
  it('shows only the history of the current screen', async () => {
    useAiChatStore.setState({
      open: true,
      historyByScreen: {
        '/ask': [turn('1', 'ask-q', 'ask-a')],
        '/analyze': [turn('2', 'analyze-q', 'analyze-a')],
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
        '/ask': [turn('1', 'ask-q', 'ask-a')],
        '/analyze': [turn('2', 'analyze-q', 'analyze-a')],
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
        '/ask': [turn('1', 'ask-q', 'ask-a')],
        '/analyze': [turn('2', 'analyze-q', 'analyze-a')],
      },
    });
    const user = userEvent.setup();
    await renderPanel('/analyze');
    await user.click(await screen.findByRole('button', { name: '全消去' }));
    await waitFor(() => expect(useAiChatStore.getState().historyByScreen).toEqual({}));
  });
});
