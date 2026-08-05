import { msg } from '@lingui/core/macro';
import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { AdminAbacSettingsPage } from './AdminAbacSettingsPage';

// SC-09, UC-05, FR-09: 管理者設定（ABAC）（05_screens: ルート /admin/abac）。
//
// 画面への到達は **platform-admin のみ**（05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」・
// §SC-09「システム管理者ロール限定」。operator も不可。IADR-0040）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// サーバ側 /bff/admin/authz も AdminOnly（BFF・後段の二重ゲート）。
//
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc09AdminAbacRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/abac',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <AdminAbacSettingsPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「管理」グループの「ABAC設定」（hi-fi モックの左レール準拠。
// 従前の実装は「管理者設定」と表示していた）。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc09AdminAbacNav: PlanNavItem = {
  id: 'sc09-admin-abac',
  label: msg`ABAC設定`,
  to: '/admin/abac',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin],
};
