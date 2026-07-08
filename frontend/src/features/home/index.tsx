import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { HomePage } from './HomePage';

// Issue #126: home feature（スケルトンの実例）。
// Issue #136: ナビ「ホーム」も featureRegistry から導出する（Layout のハードコードを廃止）。
export const homeFeature: FeatureModule = {
  id: 'home',
  routes: [{ index: true, element: <HomePage /> }],
  nav: { label: 'ホーム', to: '/' },
};
