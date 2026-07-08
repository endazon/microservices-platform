import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { HomePage } from './HomePage';

// Issue #126: home feature（スケルトンの実例）。
export const homeFeature: FeatureModule = {
  id: 'home',
  routes: [{ index: true, element: <HomePage /> }],
};
