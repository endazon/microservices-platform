import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { DocumentManagementPage } from './DocumentManagementPage';

// SC-05, UC-03, FR-06: 文書管理（05_screens: ルート /admin/documents）。管理系画面のため
// platform-admin/operator 限定（IADR-0035/IADR-0041）。詳細・版履歴は SC-03（/docs/$id）へ遷移する。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc05DocumentsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/documents',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DocumentManagementPage />
        </RequireRole>
      );
    },
  });

export const sc05DocumentsNav: NavItem = {
  id: 'sc05-documents',
  label: '文書管理',
  to: '/admin/documents',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
