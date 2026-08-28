import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo } from 'react';
import type { ReactNode } from 'react';
import { AuthContext } from './AuthContext';
import type { AuthState, SessionUser } from './AuthContext';
import { hardNavigate } from './navigation';
import { apiFetch, setUnauthorizedHandler } from '@foundation/api/apiClient';
import { appConfig } from '@foundation/config/runtimeConfig';

// NFR, ADR-0032, IADR-0273, #439: BFF セッション方式の認証状態。
//
// **SPA に認証ロジックは無い。** OIDC（認可コード + PKCE）・トークンの保管・更新・失効は
// すべて BFF が担い、SPA は `/bff/auth/me` で「誰としてログインしているか」を読むだけである。
// ブラウザが持つのは HttpOnly のセッション Cookie だけで、ここからは見えない（見えないことが正しい）。

/**
 * 現在の身元を読む。**401 は「未認証」という正常な答え**であり、エラーでも
 * 再ログイン誘導でもない（未認証の利用者は /login 画面で自分の意思でログインする）。
 * BFF 不達などその他の失敗も未認証へ倒す（フェイルクローズ。E2E のプレビュー環境には BFF が無い）。
 */
async function fetchSessionUser(): Promise<SessionUser | null> {
  try {
    return (await apiFetch<SessionUser>('/auth/me', { on401: 'silent' })) ?? null;
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  // ADR-0031 / IADR-0121: サーバー状態は TanStack Query に一元化する。身元もサーバー状態である。
  // セッションの成立・終了はトップレベル遷移（ログイン往復）を挟むため、再取得の起点は
  // ページ読み込みで足りる（フォーカス毎の refetch は既定設定に従う）。
  const { data, isPending } = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: fetchSessionUser,
    staleTime: Infinity,
  });
  const user = data ?? null;

  const login = useCallback(async (returnTo?: string) => {
    const target = returnTo ?? window.location.pathname + window.location.search;
    hardNavigate(`${appConfig().bffBaseUrl}/auth/login?returnUrl=${encodeURIComponent(target)}`);
  }, []);

  const logout = useCallback(async () => {
    // ログアウト先はセッションの sid を含むため BFF（/bff/auth/me）だけが組み立てられる。
    // 未認証（logoutUrl 無し）なら何もしない。
    if (user?.logoutUrl) hardNavigate(user.logoutUrl);
  }, [user]);

  useEffect(() => {
    // 利用中にセッションが失効した（BFF が 401 を返した）ときの再ログイン導線。
    // 認可サーバの SSO セッションが生きていればパスワード入力なしで元の場所へ戻る。
    setUnauthorizedHandler(() => {
      void login();
    });
    return () => setUnauthorizedHandler(() => {});
  }, [login]);

  const value: AuthState = useMemo(
    () => ({ user, isAuthenticated: user !== null, isLoading: isPending, login, logout }),
    [user, isPending, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
