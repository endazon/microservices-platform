import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { SearchResultsPage } from './SearchResultsPage';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧 feature。認証済みユーザー向け（ロール限定なし）。
// ABAC はサーバ側（/bff/search の deny-by-default）で適用され、権限外文書は結果に現れない。
// SC-01（AI質問）とは別に、文書詳細（SC-03）への内部遷移を担う一覧画面。?q= で検索条件を受ける。
export const sc02ResultsFeature: FeatureModule = {
  id: 'sc02-results',
  routes: [{ path: 'results', element: <SearchResultsPage /> }],
  nav: { label: '検索結果一覧', to: '/results' },
};
