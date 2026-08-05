import { msg } from '@lingui/core/macro';
import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConfigViewerPage } from './ConfigViewerPage';

// SC-11, FR-15: 構成ビューア（05_screens: ルート /admin/config-viewer）。
//
// 画面への到達は **platform-admin または platform-operator**（`ConfigViewer` 相当。IADR-0030）。
// 計画 §SC-11「管理者・運用者ロール限定。権限外にはメニュー・画面自体を表示しない」に一致する。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// サーバ側 /bff/admin/config も **404 で秘匿**する（RequireAuthorization を付けず、無認証も 404 へ寄せる。
// 付けると 401 で短絡して存在が漏れる。IADR-0029）。
//
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

// 05_screens §共通シェル: 左ナビ「運用」グループの「構成ビューア」（hi-fi モックの左レール準拠）。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc11ConfigNav: PlanNavItem = {
  id: 'sc11-config',
  label: msg`構成ビューア`,
  to: '/admin/config-viewer',
  group: 'ops',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
