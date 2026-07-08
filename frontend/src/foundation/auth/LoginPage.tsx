import { useAuth } from './useAuth';

// Issue #126: 明示ログイン画面。ボタン押下で Keycloak の認可コードフロー（PKCE）を開始する。
export function LoginPage() {
  const { login, isAuthenticated } = useAuth();

  return (
    <main style={{ maxWidth: 420, margin: '4rem auto', textAlign: 'center' }}>
      <h1>Knowledge Platform</h1>
      <p>社内ナレッジ検索・AI 回答プラットフォーム</p>
      {isAuthenticated ? (
        <p role="status">サインイン済みです。</p>
      ) : (
        <button type="button" onClick={() => void login()}>
          Keycloak でサインイン
        </button>
      )}
    </main>
  );
}
