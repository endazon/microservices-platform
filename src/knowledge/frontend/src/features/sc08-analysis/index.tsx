import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-08, UC-02, FR-07/FR-11/FR-05: AI分析ダッシュボード（05_screens: ルート /analyze）。
// **ロール限定は無い**（05_screens §共通シェル「利用者グループ（SC-01〜04・SC-08…）は
// ABAC の権限内で全利用者が利用できる」）。範囲の限定はサーバ側が narrowing-only で行う（IADR-0005）。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const AnalysisDashboardPage = lazyRouteComponent(() => import('./AnalysisDashboardPage'), 'AnalysisDashboardPage');

export const createSc08AnalysisRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/analyze',
    component: AnalysisDashboardPage,
  });

// 05_screens §共通シェル: SC-08 は番号順ではなく**利用者機能**として SC-01〜04 と同じ
// 「利用者」グループに置く（hi-fi モックの左レール準拠）。
export const sc08AnalysisNav: PlanNavItem = {
  id: 'sc08-analysis',
  label: msg`AI分析`,
  to: '/analyze',
  group: 'user',
};
