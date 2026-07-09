import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { AdminAbacSettingsPage } from './AdminAbacSettingsPage';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）feature。属性辞書・ポリシー管理は platform-admin のみ
// （Issue #135 の明示。operator も不可）。権限外は RequireRole が NotFound を描画し存在を示さない
// （IADR-0035/IADR-0040）。サーバ側 /bff/admin/authz も AdminOnly（UI は表示制御専用・サーバが実効境界）。
export const sc09AdminAbacFeature: FeatureModule = {
  id: 'sc09-admin-abac',
  routes: [
    {
      path: 'admin/abac',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <AdminAbacSettingsPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '管理者設定', to: '/admin/abac', requiresAnyRole: [PlatformRole.Admin] },
};
