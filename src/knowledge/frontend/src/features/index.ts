import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';
import { createSc01SearchRoute, sc01SearchNav } from './sc01-search';
import { createSc02ResultsRoute, sc02ResultsNav } from './sc02-results';
import { createSc03DocumentRoute } from './sc03-document';
import { createSc04WikiRoute, sc04WikiNav } from './sc04-wiki';
import { createSc05DocumentsRoute, sc05DocumentsNav } from './sc05-documents';
import { createSc06DataSourcesRoute, sc06DataSourcesNav } from './sc06-datasources';
import { createSc07ConversionsRoute, sc07ConversionsNav } from './sc07-conversions';
import { createSc08AnalysisRoute, sc08AnalysisNav } from './sc08-analysis';
import { createSc09AdminAbacRoute, sc09AdminAbacNav } from './sc09-admin-abac';
import { createSc10OperationsRoute, sc10OperationsNav } from './sc10-operations';
import { createSc11ConfigRoute, sc11ConfigNav } from './sc11-config';

// ADR-0031 / IADR-0124 決定 1: 本ユニットの画面を 1 本のタプルにして公開する。
// platform の合成点は、このタプルをスプレッドして型付きルート木へ組み込む。
//
// **戻り値へ型注釈を書いてはならない。** `readonly AnyRoute[]` などを付けた瞬間に
// ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
// （IADR-0124 §実測）。画面を足すときはタプルへ 1 行足す。
export const createKnowledgeRoutes = (shell: ShellRoute) =>
  [
    createSc01SearchRoute(shell), // SC-01 検索／チャット質問（#127）
    createSc02ResultsRoute(shell), // SC-02 検索結果一覧（#128）
    createSc03DocumentRoute(shell), // SC-03 文書詳細／プレビュー（#129）
    createSc04WikiRoute(shell), // SC-04 Wiki 閲覧導線（#130）
    createSc05DocumentsRoute(shell), // SC-05 文書管理（#131）
    createSc06DataSourcesRoute(shell), // SC-06 データソース管理（#132）
    createSc07ConversionsRoute(shell), // SC-07 変換ジョブ（#133）
    createSc08AnalysisRoute(shell), // SC-08 AI分析ダッシュボード（#134）
    createSc09AdminAbacRoute(shell), // SC-09 管理者設定（ABAC）（#135）
    createSc10OperationsRoute(shell), // SC-10 運用ダッシュボード（#136）
    createSc11ConfigRoute(shell), // SC-11 構成ビューア（#137/#138/#140）
  ] as const;

// 05_screens §共通シェル: 左ナビへ出す項目。グループ（利用者／個人／管理／運用）は各 feature が宣言する。
// SC-03 はナビに出さない（一覧・検索からの遷移で到達する）。
// 05_screens §共通シェル ［2026-08-04 確定］: 総称グループ（「その他」）を持たないため、
// グループの宣言漏れは「どのグループにも属さず静かに消える」ことを意味する。
// `PlanNavItem` で受けて tsc に落とさせる。
export const knowledgeNavItems: readonly PlanNavItem[] = [
  sc01SearchNav,
  sc02ResultsNav,
  sc04WikiNav,
  sc08AnalysisNav,
  sc05DocumentsNav,
  sc06DataSourcesNav,
  sc07ConversionsNav,
  sc09AdminAbacNav,
  sc10OperationsNav,
  sc11ConfigNav,
];
