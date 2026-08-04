import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { AnalysisDashboardPage } from './AnalysisDashboardPage';

// SC-08, UC-02, FR-07: AI分析ダッシュボード（05_screens: ルート /analyze）。一般社員向けのため
// ロール限定なし。ABAC は後段（AiAnalysisService）が narrowing-only で適用する（IADR-0005）。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc08AnalysisRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/analyze',
    component: AnalysisDashboardPage,
  });

export const sc08AnalysisNav: PlanNavItem = {
  id: 'sc08-analysis',
  label: 'AI分析',
  to: '/analyze',
  group: 'user',
};
