import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConversionJobsPage } from './ConversionJobsPage';

// SC-07, UC-06, FR-12: 変換ジョブ（05_screens: ルート /admin/conversions）。運用系画面のため
// platform-admin/operator 限定（IADR-0035/IADR-0042）。SC-06（データソース管理）からの遷移先。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc07ConversionsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/conversions',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <ConversionJobsPage />
        </RequireRole>
      );
    },
  });

export const sc07ConversionsNav: PlanNavItem = {
  id: 'sc07-conversions',
  label: '変換ジョブ',
  to: '/admin/conversions',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
