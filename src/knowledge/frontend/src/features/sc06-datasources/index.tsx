import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-06, UC-04, FR-01/FR-02: データソース管理（05_screens: ルート /admin/sources）。
// データソースは文書 ABAC のスコープ対象ではなく運用資産のため、ロールのみで制御する
// （platform-admin/operator。IADR-0039）。権限外は RequireRole が NotFound を描画して
// 画面の存在を示さない（存在秘匿。IADR-0009）。SC-07（変換ジョブ）への導線を持つ。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const DataSourceManagementPage = lazyRouteComponent(
  () => import('./DataSourceManagementPage'),
  'DataSourceManagementPage',
);

export const createSc06DataSourcesRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/sources',
    // NFR, ADR-0031 / IADR-0134: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
    // 反面 router.load() の事前読み込み（preloadRouteComponents）が効かず描画時に suspend するため、
    // このルートには Suspense 境界が要る（無いと最上位まで遡り共通シェルごと消える）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DataSourceManagementPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「管理」グループの「データソース」（hi-fi モックの左レール準拠）。
export const sc06DataSourcesNav: PlanNavItem = {
  id: 'sc06-datasources',
  label: msg`データソース`,
  to: '/admin/sources',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
