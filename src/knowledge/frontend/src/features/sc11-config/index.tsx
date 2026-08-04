import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConfigViewerPage } from './ConfigViewerPage';

// SC-11, FR-15: 構成ビューア（05_screens: ルート /admin/config-viewer）。管理者・運用者
// （ConfigViewer 相当）限定（IADR-0035/IADR-0030）。サーバ側 /bff/admin/config も 404 で秘匿する。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009/IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc11ConfigRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/config-viewer',
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <ConfigViewerPage />
        </RequireRole>
      );
    },
  });

export const sc11ConfigNav: NavItem = {
  id: 'sc11-config',
  label: '構成ビューア',
  to: '/admin/config-viewer',
  group: 'ops',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
