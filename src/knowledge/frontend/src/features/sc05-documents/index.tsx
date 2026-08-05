import { msg } from '@lingui/core/macro';
import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { DocumentManagementPage } from './DocumentManagementPage';

// SC-05, UC-03, FR-06/FR-09: 文書管理（05_screens: ルート /admin/documents）。管理系画面のため
// platform-admin/operator 限定（IADR-0039 / IADR-0041）。詳細・版履歴は SC-03（/docs/$id）へ遷移する。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009）。
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

// 05_screens §共通シェル: 左ナビ「管理」グループの「文書管理」（hi-fi モックの左レール準拠）。
export const sc05DocumentsNav: PlanNavItem = {
  id: 'sc05-documents',
  label: msg`文書管理`,
  to: '/admin/documents',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
