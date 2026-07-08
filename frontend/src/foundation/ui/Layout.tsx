import { Link, Outlet } from 'react-router-dom';
import { useAuth } from '@foundation/auth/useAuth';

// Issue #126: 認証済み領域の共通レイアウト（ナビ＋ユーザー＋サインアウト）。features は Outlet に載る。
export function Layout() {
  const { user, logout } = useAuth();
  const name =
    (user?.profile.preferred_username as string | undefined) ??
    user?.profile.name ??
    'ユーザー';

  return (
    <div>
      <header
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          padding: '0.5rem 1rem',
          borderBottom: '1px solid #ddd',
        }}
      >
        <nav style={{ display: 'flex', gap: '1rem' }}>
          <Link to="/">ホーム</Link>
        </nav>
        <div style={{ display: 'flex', gap: '0.75rem', alignItems: 'center' }}>
          <span>{name}</span>
          <button type="button" onClick={() => void logout()}>
            サインアウト
          </button>
        </div>
      </header>
      <main style={{ padding: '1rem' }}>
        <Outlet />
      </main>
    </div>
  );
}
