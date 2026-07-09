import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConversionJobsPage } from './ConversionJobsPage';

// SC-07, UC-06, FR-12: 変換ジョブ feature。変換状況・失敗一覧・人手補正（再変換）を行う運用系画面のため
// platform-admin/operator 限定（IADR-0035/IADR-0042）。権限外は RequireRole が NotFound を描画し存在を
// 示さない。SC-06（データソース管理）からの遷移先。サーバ側 /bff/conversion/jobs も同ロール。
export const sc07ConversionsFeature: FeatureModule = {
  id: 'sc07-conversions',
  routes: [
    {
      path: 'conversions',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <ConversionJobsPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '変換ジョブ', to: '/conversions', requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator] },
};
