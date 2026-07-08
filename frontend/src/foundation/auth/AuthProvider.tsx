import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import type { User, UserManager } from 'oidc-client-ts';
import { AuthContext } from './AuthContext';
import type { AuthState } from './AuthContext';
import { userManager as defaultUserManager } from './authConfig';
import { setTokenProvider, setUnauthorizedHandler } from '@foundation/api/apiClient';

// Issue #126: Keycloak OIDC のセッション管理。UserManager を購読し、現在ユーザーを state へ反映する。
// api クライアントへ「現在のアクセストークン供給元」を注入し、api は auth 実装に直接依存しない（疎結合）。
export function AuthProvider({
  children,
  manager,
}: {
  children: ReactNode;
  /** テスト用に UserManager を差し替え可能にする。 */
  manager?: UserManager;
}) {
  const mgr = useMemo(() => manager ?? defaultUserManager(), [manager]);
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    // api クライアントへトークン供給を注入（毎回最新ユーザーから取得）。
    setTokenProvider(async () => {
      const u = await mgr.getUser();
      return u && !u.expired ? u.access_token : null;
    });
    // IADR-0033: 401 の再ログイン導線を注入する。元の場所を保持して Keycloak へリダイレクトする。
    setUnauthorizedHandler(() => {
      void mgr.signinRedirect({ state: { returnTo: window.location.pathname } });
    });

    let active = true;
    mgr
      .getUser()
      .then((u) => {
        if (active) setUser(u && !u.expired ? u : null);
      })
      .finally(() => {
        if (active) setIsLoading(false);
      });

    const onLoaded = (u: User) => setUser(u);
    const onUnloaded = () => setUser(null);
    mgr.events.addUserLoaded(onLoaded);
    mgr.events.addUserUnloaded(onUnloaded);
    mgr.events.addAccessTokenExpired(onUnloaded);
    return () => {
      active = false;
      mgr.events.removeUserLoaded(onLoaded);
      mgr.events.removeUserUnloaded(onUnloaded);
      mgr.events.removeAccessTokenExpired(onUnloaded);
    };
  }, [mgr]);

  const login = useCallback(async () => {
    // 戻り先を保持してリダイレクト（認可コード + PKCE）。
    await mgr.signinRedirect({ state: { returnTo: window.location.pathname } });
  }, [mgr]);

  const logout = useCallback(async () => {
    await mgr.signoutRedirect();
  }, [mgr]);

  const value: AuthState = useMemo(
    () => ({ user, isAuthenticated: user !== null, isLoading, login, logout }),
    [user, isLoading, login, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
