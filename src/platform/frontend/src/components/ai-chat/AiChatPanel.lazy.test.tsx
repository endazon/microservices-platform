import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
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

// NFR / SC-01 / IADR-0443（#1437 作業 4）: **閉じている右レールの本体を初期チャンクへ載せない**ことの
// 回帰ガード。
//
// 何を守るか: `AiChatRail.tsx` は `@platform/ui` の Tooltip 系（＝`@base-ui/react`。実測 114 kB）を
// 静的 import する唯一のアプリ側モジュールである。誰かがこれを `AiChatPanel.tsx`（初期チャンク）へ
// 書き戻すと、**型検査もテストもビルドも通り、画面の挙動も変わらないまま**初期ロードだけが
// 114 kB 太る（`check-chunk-budget.js` の ratchet が床を超えたときに初めて赤くなるが、
// 床は「意図的に増やすとき」に更新される値なので、黙って更新されれば素通りする）。
//
// 検出のしかたは `app/routing/initialChunk.test.ts` と同じ `vi.mock` の性質
// （factory は**実際に import されたときにだけ**評価される）を使う。あちらは「読まれること」、
// こちらは「**開くまで読まれないこと**」を固定する。
//
// 🔴 **この検査は専用のファイルに置く。** 同じファイルへ他の検査を同居させると、先に走った検査が
// レールを開いてしまい（モジュール登録は 1 ファイル内で共有される）、**常に「読み込み済み」で
// 緑になる**。vitest はテストファイル単位でモジュールを分離する。

const loaded = vi.hoisted(() => ({ rail: false }));

vi.mock('./AiChatRail', async (importOriginal) => {
  loaded.rail = true;
  return await importOriginal<typeof import('./AiChatRail')>();
});

import { AiChatPanel } from './AiChatPanel';
import { useAiChatStore } from './aiChatStore';

async function renderPanel() {
  const root = createRootRoute({ component: Outlet });
  const page = createRoute({ getParentRoute: () => root, path: '$', component: AiChatPanel });
  const router = createRouter({
    routeTree: root.addChildren([page]),
    history: createMemoryHistory({ initialEntries: ['/ask'] }),
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router as never} />
      </QueryClientProvider>
    </I18nProvider>,
  );
  await act(async () => {
    await router.load();
  });
}

beforeEach(() => {
  useAiChatStore.setState({ open: false, historyByScreen: {} });
});

describe('AiChatPanel の遅延読み込み（NFR / IADR-0443）', () => {
  it('閉じている間はレール本体（Base UI を引く側）を読み込まず、開いた時に初めて読む', async () => {
    const user = userEvent.setup();
    await renderPanel();

    // 「見えるはずのもの」を先に確かめる（描画に失敗していれば以下の判定は空振りする）。
    const launcher = screen.getByRole('button', { name: 'AI チャットを開く' });
    expect(launcher).toBeInTheDocument();
    expect(loaded.rail, 'AiChatRail が初期描画で読み込まれている').toBe(false);

    await user.click(launcher);
    expect(await screen.findByRole('complementary', { name: 'AI チャットパネル' })).toBeVisible();
    expect(loaded.rail).toBe(true);
  });
});
