import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './AuthProvider';
import { useAuth } from './useAuth';
import { apiFetch } from '@foundation/api/apiClient';

// NFR, ADR-0032, IADR-0273, #439: BFF セッション方式の認証状態。
// SPA は /bff/auth/me を読むだけで、トークンに一切触れない。

const mocks = vi.hoisted(() => ({ hardNavigate: vi.fn() }));
vi.mock('./navigation', () => ({ hardNavigate: mocks.hardNavigate }));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function Probe() {
  const { user, isAuthenticated, isLoading, login, logout } = useAuth();
  if (isLoading) return <p role="status">loading</p>;
  return (
    <div>
      <p>{isAuthenticated ? `hello ${user?.name}` : 'anonymous'}</p>
      <button onClick={() => void login('/documents/42')}>login</button>
      <button onClick={() => void logout()}>logout</button>
    </div>
  );
}

function renderProvider() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 0, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <Probe />
      </AuthProvider>
    </QueryClientProvider>,
  );
}

describe('AuthProvider (BFF セッション)', () => {
  beforeEach(() => {
    mocks.hardNavigate.mockReset();
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // ★ 陽性対照: /bff/auth/me が 200 なら認証済みで、身元が useAuth から読める。
  it('reads the identity from /bff/auth/me', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () =>
      jsonResponse({
        name: 'alice',
        subject: 'sub-1',
        roles: ['platform-admin'],
        logoutUrl: '/bff/auth/logout?sid=s1',
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    renderProvider();

    expect(await screen.findByText('hello alice')).toBeInTheDocument();
    expect(String(fetchMock.mock.calls[0][0])).toContain('/bff/auth/me');
  });

  // 🔴 401 は「未認証」という正常な答え。**エラーにも再ログイン誘導にもしない**
  // （未認証の訪問者は /login 画面に着地する。勝手に認可サーバへ飛ばすと E2E も UX も壊れる）。
  it('treats 401 from /me as anonymous without navigating anywhere', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );

    renderProvider();

    expect(await screen.findByText('anonymous')).toBeInTheDocument();
    expect(mocks.hardNavigate).not.toHaveBeenCalled();
  });

  // BFF 不達（E2E のプレビュー等）も未認証へ倒す（フェイルクローズ）。
  it('treats a network failure as anonymous', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        throw new TypeError('boom');
      }),
    );

    renderProvider();

    expect(await screen.findByText('anonymous')).toBeInTheDocument();
  });

  // login() は BFF のログイン端点へ**トップレベル遷移**する（SPA 内では何も始めない）。
  it('login navigates to the BFF login endpoint with the return url', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('', { status: 401 })),
    );
    renderProvider();
    (await screen.findByText('login')).click();

    await waitFor(() =>
      expect(mocks.hardNavigate).toHaveBeenCalledWith(
        '/bff/auth/login?returnUrl=%2Fdocuments%2F42',
      ),
    );
  });

  // logout() は /me が配った logoutUrl（sid つき）へ遷移する。
  it('logout navigates to the logoutUrl handed out by /me', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse({
          name: 'alice',
          subject: 'sub-1',
          roles: [],
          logoutUrl: '/bff/auth/logout?sid=s1',
        }),
      ),
    );
    renderProvider();
    await screen.findByText('hello alice');

    screen.getByText('logout').click();

    await waitFor(() => expect(mocks.hardNavigate).toHaveBeenCalledWith('/bff/auth/logout?sid=s1'));
  });

  // 利用中のセッション失効: BFF の 401 が再ログイン導線（＝ログイン端点への遷移）を起動する。
  // /me の 'silent' とは逆向きの挙動なので、両方を対で固定する。
  it('a 401 from a later API call triggers the re-login navigation', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () =>
      jsonResponse({ name: 'alice', subject: 'sub-1', roles: [] }),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderProvider();
    await screen.findByText('hello alice');

    fetchMock.mockResolvedValueOnce(new Response('', { status: 401 }));
    await expect(apiFetch('/dashboard/summary')).rejects.toMatchObject({
      kind: 'unauthorized',
    });

    expect(mocks.hardNavigate).toHaveBeenCalledTimes(1);
    expect(String(mocks.hardNavigate.mock.calls[0][0])).toContain('/bff/auth/login?returnUrl=');
  });
});
