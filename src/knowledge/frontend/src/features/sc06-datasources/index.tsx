import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { DataSourceManagementPage } from './DataSourceManagementPage';

// SC-06, UC-04, FR-01/FR-02: データソース管理 feature。運用資産の管理のため platform-admin/operator 限定。
// 権限外は RequireRole が NotFound を描画し画面の存在を示さない（存在秘匿。IADR-0035/IADR-0039）。
// サーバ側 /bff/datasources も同ロールに限定（UI は表示制御専用・サーバが実効境界）。
export const sc06DataSourcesFeature: FeatureModule = {
  id: 'sc06-datasources',
  routes: [
    {
      path: 'datasources',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DataSourceManagementPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: 'データソース管理', to: '/datasources', requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator] },
};
