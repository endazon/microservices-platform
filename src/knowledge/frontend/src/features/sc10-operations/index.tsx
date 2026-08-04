import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { OperationsDashboardPage } from './OperationsDashboardPage';

// SC-10, UC-05, FR-10: 運用ダッシュボード（05_screens: ルート /admin/ops）。データソース
// /bff/dashboard/summary が AdminOnly のため、ルート・ナビとも platform-admin 限定（IADR-0035）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc10OperationsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/ops',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <OperationsDashboardPage />
        </RequireRole>
      );
    },
  });

export const sc10OperationsNav: NavItem = {
  id: 'sc10-operations',
  label: '運用ダッシュボード',
  to: '/admin/ops',
  group: 'ops',
  requiresAnyRole: [PlatformRole.Admin],
};
