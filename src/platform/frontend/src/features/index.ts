import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureModule, NavItem } from '@foundation/routing/featureRegistry';
// FR-14, IADR-0056: ユニット合成点 — 可変機能ユニットの画面をここで束ねる。
// ユニット追加時は src/<unit>/（frontend/ を含む）を submodule 配置し、ここへ 1 行ずつ追加する。
import { createKnowledgeRoutes, knowledgeNavItems } from '@knowledge/features';
// Issue #283, FR-14, IADR-0056/0070: AST（ai-stock-trading）ユニットの features を合成する（SC-01 設定画面ほか）。
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

/** 型付きユニットが公開するナビ項目（05_screens §共通シェル のグループ付き）。 */
export const unitNavItems: readonly NavItem[] = [...knowledgeNavItems];

/**
 * 旧契約（宣言的ルート）のまま束ねるユニット（IADR-0124 決定 2）。
 * ここに載るユニットの画面は型付きルート木の外にあり、`<Link to>` の union へは現れない。
 */
export const legacyUnitFeatures: readonly FeatureModule[] = [...aiStockTradingFeatures];
