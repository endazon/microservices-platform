import { Link, Outlet } from 'react-router-dom';
import { Button } from '@platform/ui';
import { useAuth } from '@foundation/auth/useAuth';
import { useRoles, hasAnyRole } from '@foundation/auth/roles';
import { navItems } from '@foundation/routing/nav';

// Issue #126: 認証済み領域の共通レイアウト（ナビ＋ユーザー＋サインアウト）。features は Outlet に載る。
// Issue #136 / IADR-0035: ナビは features の登録から導出し、権限外の項目は描画しない（存在秘匿）。
// ADR-0031 / IADR-0121 決定 4: 見た目は Tailwind v4 のトークン（@platform/ui）で表す。インライン style を
// 残すとユニット間でデザインが割れるため、共通シェルの骨格からトークン運用へ寄せる。
// 共通シェルの本実装（ユーザーアイコン → SC-16 遷移・通知）は移行第 2 段（#452 と同一段）で行う。
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
    <div className="min-h-screen bg-[--color-surface] text-[--color-fg]">
      <header className="flex items-center justify-between border-b border-[--color-border] px-4 py-2">
        <nav className="flex gap-4" aria-label="主要ナビゲーション">
          {items.map((i) => (
            <Link key={i.id} to={i.to} className="text-sm text-[--color-brand] hover:underline">
              {i.label}
            </Link>
          ))}
        </nav>
        <div className="flex items-center gap-3">
          <span className="text-sm text-[--color-fg-muted]">{name}</span>
          <Button size="sm" onClick={() => void logout()}>
            サインアウト
          </Button>
        </div>
      </header>
      <main className="p-4">
        <Outlet />
      </main>
    </div>
  );
}
