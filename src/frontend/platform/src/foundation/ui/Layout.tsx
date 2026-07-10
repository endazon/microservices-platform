import { Link, Outlet } from 'react-router-dom';
import { useAuth } from '@foundation/auth/useAuth';
import { useRoles, hasAnyRole } from '@foundation/auth/roles';
import { navItems } from '@foundation/routing/nav';

// Issue #126: 認証済み領域の共通レイアウト（ナビ＋ユーザー＋サインアウト）。features は Outlet に載る。
// Issue #136 / IADR-0035: ナビは features の登録から導出し、権限外の項目は描画しない（存在秘匿）。
export function Layout() {
  const { user, logout } = useAuth();
  const roles = useRoles();
  const name =
    (user?.profile.preferred_username as string | undefined) ??
    user?.profile.name ??
    'ユーザー';

  // 権限のある項目のみ表示する（requiresAnyRole 未指定は全員に表示）。
  const items = navItems().filter(
    (i) => !i.requiresAnyRole || hasAnyRole(roles, ...i.requiresAnyRole),
  );

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
          {items.map((i) => (
            <Link key={i.id} to={i.to}>
              {i.label}
            </Link>
          ))}
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
