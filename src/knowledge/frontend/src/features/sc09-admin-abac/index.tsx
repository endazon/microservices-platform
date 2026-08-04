import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { AdminAbacSettingsPage } from './AdminAbacSettingsPage';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）（05_screens: ルート /admin/abac）。属性辞書・ポリシー管理は
// platform-admin のみ（Issue #135。operator も不可。IADR-0035/IADR-0040）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc09AdminAbacRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/abac',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <AdminAbacSettingsPage />
        </RequireRole>
      );
    },
  });

export const sc09AdminAbacNav: NavItem = {
  id: 'sc09-admin-abac',
  label: '管理者設定',
  to: '/admin/abac',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin],
};
