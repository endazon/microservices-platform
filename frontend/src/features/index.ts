import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { homeFeature } from './home';
import { sc10OperationsFeature } from './sc10-operations';

// Issue #126: 有効な feature の登録簿。SC-01..11 の sub-issue はここへ 1 行追加するだけで
// 認証済みレイアウト配下にマウントされる（骨組みへの追加が疎結合）。
export const features: FeatureModule[] = [
  homeFeature,
  sc10OperationsFeature, // SC-10 運用ダッシュボード（#136）
  // 例: sc01SearchFeature, sc02ResultsFeature, ...（各 sub-issue で追加）
];
