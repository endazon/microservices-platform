import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { WikiAccessPage } from './WikiAccessPage';

// SC-04, UC-07, FR-13: Wiki 閲覧導線 feature。認証済みユーザー向け（ロール限定なし）。
// 実体は Wiki.js（ABAC ゲートウェイ経由・SSO）。本 feature は遷移導線のみを提供する。
export const sc04WikiFeature: FeatureModule = {
  id: 'sc04-wiki',
  routes: [{ path: 'wiki', element: <WikiAccessPage /> }],
  nav: { label: 'Wiki', to: '/wiki' },
};
