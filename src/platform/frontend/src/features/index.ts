import { msg } from '@lingui/core/macro';
import type { ShellRoute } from '@foundation/routing/shell';
import { legacyNavItems } from '@foundation/routing/featureRegistry';
import type {
  FeatureBreadcrumb,
  FeatureModule,
  PlanNavItem,
  UnitNavGroup,
} from '@foundation/routing/featureRegistry';
// FR-14, IADR-0056: ユニット合成点 — 可変機能ユニットの画面をここで束ねる。
// ユニット追加時は src/<unit>/（frontend/ を含む）を submodule 配置し、ここへ 1 行ずつ追加する。
import {
  createKnowledgeRoutes,
  knowledgeNavItems,
  knowledgeBreadcrumbs,
} from '@knowledge/features';
// Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの features を合成する（AST/SC-01 設定画面ほか）。
// AST は本リポジトリから変更できない別プロジェクト（IADR-0120）であり、旧契約のまま束ねる（IADR-0124 決定 2）。
import { features as aiStockTradingFeatures } from '@ai-stock-trading/features';

/**
 * 型付きルートを持つユニットの合成（IADR-0124 決定 1）。
 *
 * **戻り値に型注釈を書かない。** `readonly AnyRoute[]` を注釈した瞬間にルート ID・パスの union が
 * 失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる（IADR-0124 §実測）。
 * ユニットを足すときはタプルのスプレッドを 1 行足す。
 */
export const createUnitRoutes = (shell: ShellRoute) => [...createKnowledgeRoutes(shell)] as const;

/**
 * 本計画に属するユニットが公開するナビ項目（05_screens §共通シェル の 4 グループ付き）。
 * 型が `PlanNavItem` なので、グループの宣言漏れはここで `tsc` が落とす
 * （総称フォールバックを廃止したため、宣言漏れは「静かに消える」を意味する）。
 */
export const planNavItems: readonly PlanNavItem[] = [...knowledgeNavItems];

/**
 * ユニットが公開するパンくず宣言（05_screens §共通シェル「パンくず・権限バッジ」。#446）。
 *
 * **ナビ項目とは別の集合である**（SC-03 は左ナビに置かないがパンくずは持つ）。
 * 🔴 **旧契約ユニット（AST）はここに現れない** —— `FeatureModule` にパンくずの宣言面が無く、
 * 本リポジトリからは変更できない（IADR-0120）。結果として AST の 3 画面はパンくずを持たない。
 * **合成点が代わりに書いてやらない** —— 画面の名前と親子関係はユニットしか知らない。
 */
export const planBreadcrumbs: readonly FeatureBreadcrumb[] = [...knowledgeBreadcrumbs];

/**
 * 旧契約（宣言的ルート）のまま束ねるユニット（IADR-0124 決定 2）。
 * ここに載るユニットの画面は型付きルート木の外にあり、`<Link to>` の union へは現れない。
 */
export const legacyUnitFeatures: readonly FeatureModule[] = [...aiStockTradingFeatures];

/**
 * 本計画に属さない可変機能ユニットの左ナビグループ
 * （05_screens §共通シェル ［2026-08-04 確定］。IADR-0125 決定 9）。
 *
 * 計画は「グループ名は**ユニットの機能名**とする（例: `ai-stock-trading` → 「株式自動売買」）。
 * **総称としての『その他』は使わない**」と定めた。ユニット自身（AST）は本リポジトリから
 * 変更できない（IADR-0120）ため、**機能名は合成点が与える**——合成点はユニットを知る
 * 唯一の場所であり（IADR-0124 決定 1）、ここ以外に置くと foundation が可変ユニットを知ることになる。
 *
 * ユニットを足すときは、ルート（`createUnitRoutes`）・ナビ項目（`planNavItems`）と同様に
 * ここへ 1 要素足す。**機能名を書かずに済ませる逃げ道は用意しない**（総称は使えない）。
 */
export const unitNavGroups: readonly UnitNavGroup[] = [
  {
    id: 'ai-stock-trading',
    // 計画（05_screens §共通シェル）が例示した機能名そのもの。
    label: msg`株式自動売買`,
    items: legacyNavItems(aiStockTradingFeatures),
  },
];
