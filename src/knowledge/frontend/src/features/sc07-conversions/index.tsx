import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-07, UC-06, FR-12: 変換ジョブ（05_screens: ルート /admin/conversions）。SC-06 からの遷移先。
//
// 画面への到達は platform-admin/operator（IADR-0039 / IADR-0042）。権限外は RequireRole が
// NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009）。
// **再変換の実行だけは platform-admin に限る**（05_screens §SC-07 2026-08-04 確定。IADR-0127 決定 1・
// API 側は IADR-0128 決定 1）——絞り込みは画面の中（ConversionJobsPage）で行う。
// ルートごと admin 限定にすると、計画が確定した範囲（再変換の実行権限）を超えて
// 運用者の閲覧まで奪うことになる（閲覧ロールの差異は planning#198 提案 8 で裁定待ち）。
//
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0133: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const ConversionJobsPage = lazyRouteComponent(() => import('./ConversionJobsPage'), 'ConversionJobsPage');

export const createSc07ConversionsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/conversions',
    // NFR, ADR-0031 / IADR-0133: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
    // 反面 router.load() の事前読み込み（preloadRouteComponents）が効かず描画時に suspend するため、
    // このルートには Suspense 境界が要る（無いと最上位まで遡り共通シェルごと消える）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <ConversionJobsPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「管理」グループの「変換ジョブ」（hi-fi モックの左レール準拠）。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc07ConversionsNav: PlanNavItem = {
  id: 'sc07-conversions',
  label: msg`変換ジョブ`,
  to: '/admin/conversions',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
