import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { createSc01SearchRoute, sc01SearchNav, sc01SearchBreadcrumb } from './sc01-search';
import { createSc02ResultsRoute, sc02ResultsNav, sc02ResultsBreadcrumb } from './sc02-results';
import { createSc03DocumentRoute, sc03DocumentBreadcrumb } from './sc03-document';
import { createSc04WikiRoute, sc04WikiNav, sc04WikiBreadcrumb } from './sc04-wiki';
import {
  createSc05DocumentsRoute,
  sc05DocumentsNav,
  sc05DocumentsBreadcrumb,
} from './sc05-documents';
import {
  createSc06DataSourcesRoute,
  sc06DataSourcesNav,
  sc06DataSourcesBreadcrumb,
} from './sc06-datasources';
import {
  createSc07ConversionsRoute,
  sc07ConversionsNav,
  sc07ConversionsBreadcrumb,
} from './sc07-conversions';
import { createSc08AnalysisRoute, sc08AnalysisNav, sc08AnalysisBreadcrumb } from './sc08-analysis';
import {
  createSc09AdminAbacRoute,
  sc09AdminAbacNav,
  sc09AdminAbacBreadcrumb,
} from './sc09-admin-abac';
import {
  createSc10OperationsRoute,
  sc10OperationsNav,
  sc10OperationsBreadcrumb,
} from './sc10-operations';
import { createSc11ConfigRoute, sc11ConfigNav, sc11ConfigBreadcrumb } from './sc11-config';
import {
  createSc12McpClientsRoute,
  sc12McpClientsNav,
  sc12McpClientsBreadcrumb,
} from './sc12-mcp-clients';
import { createSc17UsersRoute, sc17UsersNav, sc17UsersBreadcrumb } from './sc17-users';
import { createSc18GraphRoute, sc18GraphNav, sc18GraphBreadcrumb } from './sc18-graph';
import {
  createSc19PrivateNotesRoute,
  sc19PrivateNotesNav,
  sc19PrivateNotesBreadcrumb,
} from './sc19-private-notes';
import {
  createSc20ObsidianSettingsRoute,
  sc20ObsidianSettingsNav,
  sc20ObsidianSettingsBreadcrumb,
} from './sc20-obsidian-settings';
import {
  createSc21AiSuggestionsRoute,
  sc21AiSuggestionsNav,
  sc21AiSuggestionsBreadcrumb,
} from './sc21-ai-suggestions';

// ADR-0031 / IADR-0124 決定 1: 本ユニットの画面を 1 本のタプルにして公開する。
// platform の合成点は、このタプルをスプレッドして型付きルート木へ組み込む。
//
// **戻り値へ型注釈を書いてはならない。** `readonly AnyRoute[]` などを付けた瞬間に
// ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
// （IADR-0124 §実測）。画面を足すときはタプルへ 1 行足す。
export const createKnowledgeRoutes = (shell: ShellRoute) =>
  [
    createSc01SearchRoute(shell), // SC-01 検索／チャット質問（#127 → 新スタックで再実装 #502）
    createSc02ResultsRoute(shell), // SC-02 検索結果一覧（#128 → 新スタックで再実装 #502）
    createSc03DocumentRoute(shell), // SC-03 文書詳細／プレビュー（#129 → 新スタックで再実装 #502）
    createSc04WikiRoute(shell), // SC-04 Wiki 閲覧導線（#130）
    createSc05DocumentsRoute(shell), // SC-05 文書管理（#131）
    createSc06DataSourcesRoute(shell), // SC-06 データソース管理（#132）
    createSc07ConversionsRoute(shell), // SC-07 変換ジョブ（#133）
    createSc08AnalysisRoute(shell), // SC-08 AI分析ダッシュボード（#134）
    createSc09AdminAbacRoute(shell), // SC-09 管理者設定（ABAC）（#135 → 新スタックで再実装 #504）
    createSc10OperationsRoute(shell), // SC-10 運用ダッシュボード（#136 → 新スタックで再実装 #504）
    createSc11ConfigRoute(shell), // SC-11 構成ビューア（#137/#138/#140 → 新スタックで再実装 #504）
    createSc12McpClientsRoute(shell), // SC-12 MCP クライアント登録管理（#452。公開ツールは参照のみ）
    createSc17UsersRoute(shell), // SC-17 ユーザーアカウント管理（#452。新規作成は持たない）
    createSc18GraphRoute(shell), // SC-18 ナレッジグラフビュー（#917）
    createSc19PrivateNotesRoute(shell), // SC-19 個人資料管理（#451。本文編集は持たない）
    createSc20ObsidianSettingsRoute(shell), // SC-20 Obsidian 連携設定（#451）
    createSc21AiSuggestionsRoute(shell), // SC-21 AI 提案一覧（#918。承認は SC-03 経由）
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
  sc18GraphNav,
  sc21AiSuggestionsNav,
  // 05_screens §共通シェル: 「個人」グループ（本人の資料だけを扱い、組織の文書を扱う
  // 「利用者」グループとは対象範囲が異なる）。この 2 件が同グループの最初の住人である。
  sc19PrivateNotesNav,
  sc20ObsidianSettingsNav,
  sc05DocumentsNav,
  sc06DataSourcesNav,
  sc07ConversionsNav,
  sc09AdminAbacNav,
  sc10OperationsNav,
  sc11ConfigNav,
  sc12McpClientsNav,
  sc17UsersNav,
];

/**
 * 本ユニットの画面のパンくず宣言（05_screens §共通シェル「パンくず・権限バッジ」。#446）。
 *
 * 🔴 **ナビ項目（`knowledgeNavItems`）とは別の集合である。** SC-03 は左ナビに置かない
 * （計画 §共通シェル ［2026-08-05 確定］）が、パンくずは持つ——両者を 1 つの配列で
 * 兼ねると SC-03 を表現できない。**画面を足したらここへ 1 行足す**（足し忘れた画面は
 * パンくずが出ないだけで静かに通るので、`breadcrumbs.test.ts` が網羅を固定する）。
 *
 * 並びは SC 番号順（左ナビと違い、順序は描画に影響しない——宣言は `routePath` で引かれる）。
 */
export const knowledgeBreadcrumbs: readonly FeatureBreadcrumb[] = [
  sc01SearchBreadcrumb,
  sc02ResultsBreadcrumb,
  sc03DocumentBreadcrumb,
  sc04WikiBreadcrumb,
  sc05DocumentsBreadcrumb,
  sc06DataSourcesBreadcrumb,
  sc07ConversionsBreadcrumb,
  sc08AnalysisBreadcrumb,
  sc09AdminAbacBreadcrumb,
  sc10OperationsBreadcrumb,
  sc11ConfigBreadcrumb,
  sc12McpClientsBreadcrumb,
  sc17UsersBreadcrumb,
  sc18GraphBreadcrumb,
  sc19PrivateNotesBreadcrumb,
  sc20ObsidianSettingsBreadcrumb,
  sc21AiSuggestionsBreadcrumb,
];
