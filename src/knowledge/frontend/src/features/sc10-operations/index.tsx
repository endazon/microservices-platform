import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-10, UC-05, FR-10: 運用ダッシュボード（05_screens: ルート /admin/ops）。
//
// 画面への到達は **platform-admin のみ**。**計画 §SC-10 は「運用者・管理者ロール限定」と定めるが、
// データ源 /bff/dashboard/summary も後段 DashboardService も AdminOnly** であり、画面だけ広げると
// 運用者に「開くと必ず 403 になる画面」を見せることになる（IADR-0129 決定 4）。
// 是正の向き（計画の改訂か実装の是正か）は計画側の裁定を要するため据え置き、環流記録の提案 7 で問う。
//
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const OperationsDashboardPage = lazyRouteComponent(
  () => import('./OperationsDashboardPage'),
  'OperationsDashboardPage',
);

export const createSc10OperationsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/ops',
    // NFR, ADR-0031 / IADR-0134: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
    // 反面 router.load() の事前読み込み（preloadRouteComponents）が効かず描画時に suspend するため、
    // このルートには Suspense 境界が要る（無いと最上位まで遡り共通シェルごと消える）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <OperationsDashboardPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「運用」グループの「ダッシュボード」（hi-fi モックの左レール準拠。
// 従前の実装は「運用ダッシュボード」と表示していた）。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc10OperationsNav: PlanNavItem = {
  id: 'sc10-operations',
  label: msg`ダッシュボード`,
  to: '/admin/ops',
  group: 'ops',
  requiresAnyRole: [PlatformRole.Admin],
};
