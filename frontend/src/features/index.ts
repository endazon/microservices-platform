import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { homeFeature } from './home';
import { sc01SearchFeature } from './sc01-search';
import { sc02ResultsFeature } from './sc02-results';
import { sc03DocumentFeature } from './sc03-document';
import { sc04WikiFeature } from './sc04-wiki';
import { sc05DocumentsFeature } from './sc05-documents';
import { sc06DataSourcesFeature } from './sc06-datasources';
import { sc07ConversionsFeature } from './sc07-conversions';
import { sc08AnalysisFeature } from './sc08-analysis';
import { sc09AdminAbacFeature } from './sc09-admin-abac';
import { sc10OperationsFeature } from './sc10-operations';
import { sc11ConfigFeature } from './sc11-config';

// Issue #126: 有効な feature の登録簿。SC-01..11 の sub-issue はここへ 1 行追加するだけで
// 認証済みレイアウト配下にマウントされる（骨組みへの追加が疎結合）。
export const features: FeatureModule[] = [
  homeFeature,
  sc01SearchFeature, // SC-01 検索／チャット質問（#127）
  sc02ResultsFeature, // SC-02 検索結果一覧（#128）
  sc03DocumentFeature, // SC-03 文書詳細／プレビュー（#129）
  sc04WikiFeature, // SC-04 Wiki 閲覧導線（#130）
  sc05DocumentsFeature, // SC-05 文書管理（#131）
  sc06DataSourcesFeature, // SC-06 データソース管理（#132）
  sc07ConversionsFeature, // SC-07 変換ジョブ（#133）
  sc08AnalysisFeature, // SC-08 AI分析ダッシュボード（#134）
  sc09AdminAbacFeature, // SC-09 管理者設定（ABAC）（#135）
  sc10OperationsFeature, // SC-10 運用ダッシュボード（#136）
  sc11ConfigFeature, // SC-11 構成ビューア（#137/#138/#140）
  // 例: sc02ResultsFeature, sc03DocumentFeature, ...（各 sub-issue で追加）
];
