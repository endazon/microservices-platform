import { msg } from '@lingui/core/macro';
import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';
import { ConversionJobsPage } from './ConversionJobsPage';

// SC-07, UC-06, FR-12: 変換ジョブ（05_screens: ルート /admin/conversions）。SC-06 からの遷移先。
//
// 画面への到達は platform-admin/operator（IADR-0039 / IADR-0042）。権限外は RequireRole が
// NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009）。
// **再変換の実行だけは platform-admin に限る**（05_screens §SC-07 2026-08-04 確定。IADR-0127 決定 1）
// ——絞り込みは画面の中（ConversionJobsPage）で行う。ルートごと admin 限定にすると、
// 計画が確定した範囲（再変換の実行権限）を超えて運用者の閲覧まで奪うことになる。
//
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc07ConversionsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/conversions',
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
