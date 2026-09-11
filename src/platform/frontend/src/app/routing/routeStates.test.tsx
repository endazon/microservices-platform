import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { RouterContextProvider } from '@tanstack/react-router';
import { ApiError } from '@foundation/api/ApiError';
import { router } from './router';
import { ENTRY_ROUTE_PATH } from './entryPath';
import { RouteErrorComponent, RoutePendingComponent } from './routeStates';

// ［2026-09-12 / UI/UX 改善 裁定 6］画面単位のエラー境界・待ち表示・先読みの配線。
//
// `RouteErrorComponent` は `<Link>` と `useRouter()` を使うので、**実アプリのルータの
// context の中で**描く（`RouterContextProvider` は context だけを与え、ルートは描かない）。

function renderInRouterContext(ui: React.ReactNode) {
  return render(<RouterContextProvider router={router}>{ui}</RouterContextProvider>);
}

describe('RouteErrorComponent（defaultErrorComponent）', () => {
  // IADR-0009: ApiError は「不在／権限なし」を区別しない中立メッセージを自ら持つ。
  it('shows the ApiError message as-is', () => {
    renderInRouterContext(
      <RouteErrorComponent
        error={new ApiError('notFound', '該当する情報が見つかりませんでした。', null)}
        reset={() => {}}
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('該当する情報が見つかりませんでした。');
  });

  // 想定外の例外は中立の既定文言へ倒す（内部事情を画面へ出さない）。
  it('falls back to the neutral message and hides the internal detail', () => {
    renderInRouterContext(
      <RouteErrorComponent error={new Error('internal detail leaked')} reset={() => {}} />,
    );
    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent(
      '予期しないエラーが発生しました。時間をおいて再度お試しください。',
    );
    expect(alert).not.toHaveTextContent('internal detail leaked');
  });

  // 🔴 **`reset()` だけでは押しても何も起きない。** 失敗したデータをそのまま描き直すため、
  // ルータのデータを無効化して読み直させる（両方呼ぶことが「再読み込み」の意味である）。
  it('resets the boundary and invalidates the router data on 再読み込み', async () => {
    const reset = vi.fn();
    const invalidate = vi.spyOn(router, 'invalidate').mockResolvedValue(undefined);
    try {
      renderInRouterContext(<RouteErrorComponent error={new Error('boom')} reset={reset} />);
      await userEvent.click(screen.getByRole('button', { name: '再読み込み' }));
      expect(reset).toHaveBeenCalledTimes(1);
      expect(invalidate).toHaveBeenCalledTimes(1);
    } finally {
      invalidate.mockRestore();
    }
  });

  // ここは**ルータの中**なので SPA 遷移で足りる（全体の読み直しをさせない）。
  it('links ホームへ戻る at the entry route (SPA 遷移)', () => {
    renderInRouterContext(<RouteErrorComponent error={new Error('boom')} reset={() => {}} />);
    expect(screen.getByRole('link', { name: 'ホームへ戻る' })).toHaveAttribute(
      'href',
      ENTRY_ROUTE_PATH,
    );
  });

  // 読み上げの器は 1 つだけ（`ErrorState` が `role="alert"` を持つ）。
  it('exposes exactly one alert region', () => {
    renderInRouterContext(<RouteErrorComponent error={new Error('boom')} reset={() => {}} />);
    expect(screen.getAllByRole('alert')).toHaveLength(1);
  });
});

describe('RoutePendingComponent（defaultPendingComponent）', () => {
  // 待ちは**見える文言 ＋ 読み上げ名**の両方を持つ（`LoadingState` が輪の sr-only ラベルと
  // `aria-hidden` の見える文言を組む）。片方だけだと、目で見えないか読み上げられないかのどちらかになる。
  it('announces what is being waited for, in both channels', () => {
    render(<RoutePendingComponent />);
    const labels = screen.getAllByText('読み込み中…');
    expect(labels).toHaveLength(2);
    expect(labels.some((el) => el.classList.contains('sr-only'))).toBe(true);
    expect(labels.some((el) => el.getAttribute('aria-hidden') === 'true')).toBe(true);
  });
});

// 配線そのもの。**画面単位の境界は `createRouter` の既定に載っていて初めて効く**——
// コンポーネントが正しくても、ルータへ渡っていなければ 1 画面の失敗がアプリ全体を落とす。
describe('router defaults (裁定 6)', () => {
  it('uses the screen-level error and pending components', () => {
    expect(router.options.defaultErrorComponent).toBe(RouteErrorComponent);
    expect(router.options.defaultPendingComponent).toBe(RoutePendingComponent);
  });

  // 遷移時の先読み。`'render'`（描画と同時に全リンク）は採らない——左レールの十数本が
  // 画面を開いただけで全部走る。
  it('preloads on intent (hover / focus), not on render', () => {
    expect(router.options.defaultPreload).toBe('intent');
  });
});
