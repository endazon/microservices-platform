import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConfigViewerPage } from './ConfigViewerPage';

// SC-11, FR-15: 構成ビューア feature。
// アクセスは管理者・運用者（ConfigViewer 相当）に限定し、権限外は RequireRole が NotFound を描画して
// 画面の存在を示さない（存在秘匿。IADR-0009/IADR-0034）。サーバ側 /bff/admin/config も 404 で秘匿する。
// ルート・ナビとも ConfigViewer 相当ロールに限定する（#140 でテスト観点を展開）。
export const sc11ConfigFeature: FeatureModule = {
  id: 'sc11-config',
  routes: [
    {
      path: 'config',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <ConfigViewerPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '構成ビューア', to: '/config', requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator] },
};
