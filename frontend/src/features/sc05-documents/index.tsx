import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { DocumentManagementPage } from './DocumentManagementPage';

// SC-05, UC-03, FR-06: 文書管理 feature。文書 CRUD・属性/タグ・公開/アーカイブを行う管理系画面のため
// platform-admin/operator 限定（IADR-0035/IADR-0041）。権限外は RequireRole が NotFound を描画し存在を
// 示さない。詳細・版履歴は SC-03（/documents/:id）へ遷移する。サーバ側 /bff/documents 書き込みも同ロール。
export const sc05DocumentsFeature: FeatureModule = {
  id: 'sc05-documents',
  routes: [
    {
      path: 'documents',
      element: (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DocumentManagementPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '文書管理', to: '/documents', requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator] },
};
