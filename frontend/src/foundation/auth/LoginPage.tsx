import { Navigate } from 'react-router-dom';
import { useAuth } from './useAuth';

// Issue #126: 明示ログイン画面。ボタン押下で Keycloak の認可コードフロー（PKCE）を開始する。
// 既に認証済みならホームへ誘導する（ログイン画面に留まらない）。
export function LoginPage() {
  const { login, isAuthenticated } = useAuth();

  if (isAuthenticated) {
    return <Navigate to="/" replace />;
  }

  return (
    <main style={{ maxWidth: 420, margin: '4rem auto', textAlign: 'center' }}>
      <h1>Knowledge Platform</h1>
      <p>社内ナレッジ検索・AI 回答プラットフォーム</p>
      <button type="button" onClick={() => void login()}>
        Keycloak でサインイン
      </button>
    </main>
  );
}
