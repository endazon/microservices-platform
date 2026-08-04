import { Link, Outlet } from '@tanstack/react-router';
import { CircleUserRound } from 'lucide-react';
import { Button } from '@platform/ui';
import { useAuth } from '@foundation/auth/useAuth';
import { useRoles, hasAnyRole } from '@foundation/auth/roles';
import { navGroups } from '@foundation/routing/nav';
import type { NavItem } from '@foundation/routing/nav';
import { appConfig } from '@foundation/config/runtimeConfig';
import { Notifications } from './notifications';

// Issue #126 / 05_screens §共通シェル: 認証済み領域の共通シェル。features は Outlet に載る。
// Issue #136 / IADR-0035: ナビはユニットの登録から導出し、権限外の項目は描画しない（存在秘匿）。
// ADR-0031 / IADR-0121 決定 4: 見た目は Tailwind v4 のトークン（@platform/ui）で表す。
//
// 本シェルが持つのは 05_screens §共通シェル のうち #490 の範囲——ブランド表示名・左ナビ（4 グループ）・
// ユーザーアイコン（→ SC-16）・通知——である。パンくず・権限バッジは #452、
// 右レール AI チャットパネルは移行第 4 段（IADR-0121 決定 5）。

/** SC-16 アカウント設定（Keycloak アカウントコンソール）の URL。実行時 config から組み立てる。 */
export function accountConsoleUrl(authority: string): string {
  return `${authority.replace(/\/+$/, '')}/account`;
}

function NavLink({ item }: { item: NavItem }) {
  // IADR-0124 決定 5: ナビはユニットが公開する**データ**であり、`to` は string 型のため
  // TanStack の型付き union では検査できない。到達性は router.test.ts が実行時に固定する。
  return (
    <Link
      to={item.to as '/ask'}
      className="block rounded px-2 py-1 text-sm text-[--color-brand] hover:underline"
    >
      {item.label}
    </Link>
  );
}

export function Layout() {
  const { user, logout } = useAuth();
  const roles = useRoles();
  const name =
    (user?.profile.preferred_username as string | undefined) ?? user?.profile.name ?? 'ユーザー';

  // 権限のある項目のみ表示する（requiresAnyRole 未指定は全員に表示）。
  // 絞り込みの結果 0 件になったグループは見出しごと落とす（存在秘匿。IADR-0035）。
  const groups = navGroups()
    .map((g) => ({
      ...g,
      items: g.items.filter((i) => !i.requiresAnyRole || hasAnyRole(roles, ...i.requiresAnyRole)),
    }))
    .filter((g) => g.items.length > 0);

  return (
    <div className="min-h-screen bg-[--color-surface] text-[--color-fg]">
      <header className="flex items-center justify-between border-b border-[--color-border] px-4 py-2">
        {/* 05_screens §共通シェル: ブランド表示名は「汎用プラットフォーム」で統一する。 */}
        <span className="text-sm font-semibold text-[--color-fg]">汎用プラットフォーム</span>
        <div className="flex items-center gap-3">
          {/* 05_screens §共通シェル: ユーザーアイコンから SC-16（アカウント設定）へ遷移する。
              SC-16 は Keycloak テーマ＝別ホスト配信のため、SPA のルータではなく外部遷移で開く。 */}
          <a
            href={accountConsoleUrl(appConfig().oidc.authority)}
            className="flex items-center gap-1.5 text-sm text-[--color-fg-muted] hover:underline"
            aria-label={`アカウント設定（${name}）`}
          >
            <CircleUserRound className="size-5" aria-hidden />
            <span>{name}</span>
          </a>
          <Button size="sm" onClick={() => void logout()}>
            サインアウト
          </Button>
        </div>
      </header>
      <div className="flex">
        <nav
          className="w-56 shrink-0 border-r border-[--color-border] p-3"
          aria-label="主要ナビゲーション"
        >
          {groups.map((g) => (
            <div key={g.id} className="mb-4">
              <h2 className="mb-1 px-2 text-xs font-semibold tracking-wide text-[--color-fg-muted]">
                {g.label}
              </h2>
              <ul>
                {g.items.map((i) => (
                  <li key={i.id}>
                    <NavLink item={i} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        <main className="grow p-4">
          <Outlet />
        </main>
      </div>
      <Notifications />
    </div>
  );
}
