import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { OperationsDashboardPage } from './OperationsDashboardPage';

// SC-10, UC-05, FR-10: 運用ダッシュボード feature。
// データソース /bff/dashboard/summary は AdminOnly のため、ルート・ナビとも platform-admin 限定。
// 権限外は RequireRole が NotFound を描画し画面の存在を示さない（存在秘匿。IADR-0035）。
export const sc10OperationsFeature: FeatureModule = {
  id: 'sc10-operations',
  routes: [
    {
      path: 'ops',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <OperationsDashboardPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '運用', to: '/ops', requiresAnyRole: [PlatformRole.Admin] },
};
