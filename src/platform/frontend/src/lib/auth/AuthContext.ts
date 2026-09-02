import { createContext } from 'react';

// NFR, ADR-0032, IADR-0273, #439: 認証状態のコンテキスト。features は useAuth 経由でのみ参照する。
//
// **SPA はトークンを扱わない**（BFF セッション方式）。身元は `/bff/auth/me` が返すものが全てで、
// ブラウザが持つ資格情報は HttpOnly のセッション Cookie だけである。

/** `/bff/auth/me` が返す現在の身元。**トークンは含まれない。** */
export interface SessionUser {
  /** 表示名（認可サーバの preferred_username）。 */
  name: string;
  /** 認可サーバ上の一意な識別子（sub）。 */
  subject: string;
  /** レルムロール。ロール判定（useRoles / RequireRole）の一次情報。 */
  roles: string[];
  /** ログアウト先（セッションの sid を含む。BFF だけが正しく組み立てられる）。 */
  logoutUrl?: string | null;
  // ［2026-09-03 / AST#414］旧方式（ブラウザ内トークン）互換の `access_token` は消えた。
  // IADR-0273 決定 7 が「`ai-stock-trading` submodule のテストが旧形の値を流し込むため残す。
  // AST 側が追随したらこのフォールバックごと削る」と定めていた条件が満たされたためである。
}

export interface AuthState {
  user: SessionUser | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  /** BFF のログイン端点へトップレベル遷移する（認可コード + PKCE は BFF が実施する）。 */
  login: (returnTo?: string) => Promise<void>;
  /** BFF のログアウト端点へトップレベル遷移する（ブラウザと認可サーバの両セッションを終える）。 */
  logout: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);
