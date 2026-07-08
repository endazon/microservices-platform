import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { SearchChatPage } from './SearchChatPage';

// SC-01, UC-01, FR-03/FR-04: 検索／チャット質問画面 feature（本システムの主入口）。
// 認証済みユーザー向け（ロール限定なし）。ABAC は後段（BFF/検索/AI）が narrowing・deny-by-default で適用。
export const sc01SearchFeature: FeatureModule = {
  id: 'sc01-search',
  routes: [{ path: 'search', element: <SearchChatPage /> }],
  nav: { label: '検索 / AI質問', to: '/search' },
};
