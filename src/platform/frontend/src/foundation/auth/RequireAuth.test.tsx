import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { AuthContext } from './AuthContext';
import type { AuthState } from './AuthContext';
import { RequireAuth } from './RequireAuth';

// Issue #126: 認証ガードの挙動（未認証は /login へ誘導・解決中は読み込み表示・認証済みは子を表示）。
// ADR-0031 / IADR-0124: ルータは TanStack Router。ガードは実ルータの上で検証する
// （Navigate による遷移が実際に起きることまで見るため、素の描画では代替しない）。
function renderGuard(state: Partial<AuthState>) {
  const value: AuthState = {
    user: null,
    isAuthenticated: false,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
    ...state,
  };

  const rootRoute = createRootRoute({ component: Outlet });
  const secretRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/secret',
    component: () => (
      <RequireAuth>
        <div>SECRET CONTENT</div>
      </RequireAuth>
    ),
  });
  const loginRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: '/login',
    // RequireAuth は遷移元を ?from= で渡す。受け取れる形にしておく。
    validateSearch: (raw: Record<string, unknown>): { from?: string } =>
      typeof raw.from === 'string' ? { from: raw.from } : {},
    component: () => <div>LOGIN PAGE</div>,
  });
  const router = createRouter({
    routeTree: rootRoute.addChildren([secretRoute, loginRoute]),
    history: createMemoryHistory({ initialEntries: ['/secret'] }),
  });

  return {
    router,
    ...render(
      <AuthContext.Provider value={value}>
        {/* テスト専用のルータ木のため、アプリの Register 型とは別物として渡す。 */}
        <RouterProvider router={router as never} />
      </AuthContext.Provider>,
    ),
  };
}

describe('RequireAuth', () => {
  it('redirects unauthenticated users to /login', async () => {
    const { router } = renderGuard({ isAuthenticated: false, isLoading: false });
    expect(await screen.findByText('LOGIN PAGE')).toBeInTheDocument();
    expect(screen.queryByText('SECRET CONTENT')).not.toBeInTheDocument();
    // 遷移元は検索パラメータで保持する（リロード・共有でも失われない）。
    expect(router.state.location.search).toEqual({ from: '/secret' });
  });

  it('shows a loading indicator while auth state resolves', async () => {
    renderGuard({ isLoading: true });
    expect(await screen.findByRole('status')).toHaveTextContent('読み込み中');
  });

  it('renders children when authenticated', async () => {
    renderGuard({ isAuthenticated: true, isLoading: false });
    expect(await screen.findByText('SECRET CONTENT')).toBeInTheDocument();
  });
});
