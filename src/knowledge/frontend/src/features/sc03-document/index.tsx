import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { DocumentDetailPage } from './DocumentDetailPage';

// SC-03, UC-01/UC-07, FR-06: 文書詳細／プレビュー feature。認証済みユーザー向け（ロール限定なし）。
// 検索結果一覧（SC-02）・文書管理（SC-05）から `/documents/:id` へ遷移する詳細画面。ナビには
// 出さない（一覧・検索からの遷移で到達する）。ABAC はサーバ側（BFF）で適用され、権限外は 404 秘匿。
export const sc03DocumentFeature: FeatureModule = {
  id: 'sc03-document',
  routes: [{ path: 'documents/:id', element: <DocumentDetailPage /> }],
};
