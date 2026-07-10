import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { AnalysisDashboardPage } from './AnalysisDashboardPage';

// SC-08, UC-02, FR-07: AI分析ダッシュボード feature。
// 一般社員向け（UC-02）のためロール限定なし。認証必須（RequireAuth 配下）。
// ABAC は後段（AiAnalysisService）が narrowing-only で適用する（IADR-0005）。
export const sc08AnalysisFeature: FeatureModule = {
  id: 'sc08-analysis',
  routes: [{ path: 'analysis', element: <AnalysisDashboardPage /> }],
  nav: { label: 'AI分析', to: '/analysis' },
};
