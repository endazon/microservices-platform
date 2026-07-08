import { createContext } from 'react';
import type { User } from 'oidc-client-ts';

// Issue #126: 認証状態のコンテキスト。features は useAuth 経由でのみ参照する。
export interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  /** Keycloak の認可コードフロー（PKCE）を開始する。 */
  login: () => Promise<void>;
  /** サインアウトして Keycloak のログアウトへリダイレクトする。 */
  logout: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);
