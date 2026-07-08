import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { homeFeature } from './home';
import { sc01SearchFeature } from './sc01-search';
import { sc08AnalysisFeature } from './sc08-analysis';
import { sc10OperationsFeature } from './sc10-operations';
import { sc11ConfigFeature } from './sc11-config';

// Issue #126: 有効な feature の登録簿。SC-01..11 の sub-issue はここへ 1 行追加するだけで
// 認証済みレイアウト配下にマウントされる（骨組みへの追加が疎結合）。
export const features: FeatureModule[] = [
  homeFeature,
  sc01SearchFeature, // SC-01 検索／チャット質問（#127）
  sc08AnalysisFeature, // SC-08 AI分析ダッシュボード（#134）
  sc10OperationsFeature, // SC-10 運用ダッシュボード（#136）
  sc11ConfigFeature, // SC-11 構成ビューア（#137/#138/#140）
  // 例: sc02ResultsFeature, sc03DocumentFeature, ...（各 sub-issue で追加）
];
