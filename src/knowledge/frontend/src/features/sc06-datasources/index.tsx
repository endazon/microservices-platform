import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { DataSourceManagementPage } from './DataSourceManagementPage';

// SC-06, UC-04, FR-01/FR-02: データソース管理（05_screens: ルート /admin/sources）。
// 運用資産の管理のため platform-admin/operator 限定（IADR-0035/IADR-0039）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc06DataSourcesRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/sources',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DataSourceManagementPage />
        </RequireRole>
      );
    },
  });

export const sc06DataSourcesNav: NavItem = {
  id: 'sc06-datasources',
  label: 'データソース管理',
  to: '/admin/sources',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
