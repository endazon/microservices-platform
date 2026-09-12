import { msg } from '@lingui/core/macro';
import type { ShellRoute } from '@foundation/routing/shell';
import type {
  FeatureBreadcrumb,
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
// AST は本リポジトリから変更できない別プロジェクト（IADR-0120）だが、**AST#414 で型付きルート契約へ移った**
// ため、旧契約の互換ブリッジ（`createLegacyRoutes`。IADR-0124 決定 2）を経由しなくなった。
import { createAiStockTradingRoutes, aiStockTradingNavItems } from '@ai-stock-trading/features';
import * as aiStockTradingUnit from '@ai-stock-trading/features';
import { registerUnitMessages } from '@foundation/i18n';
import type { Locale } from '@foundation/i18n';
import type { Messages } from '@lingui/core';

// AST の文言カタログ（UI/UX 改善 2026-09-12・利用者裁定 #3「Lingui を導入するが英訳はしない」）。
// ルート・ナビ・パンくずと同じく、**ユニットを知る唯一の場所**であるここで基盤の i18n へ束ねる
// （`lib/i18n` が `@ai-stock-trading` を import する形は ESLint が禁じており、foundation は
// 可変ユニットを知らない）。AST は ja しか持たないので、en には ja が流れる（理由は `registerUnitMessages`）。
// 順序: `@foundation/i18n` はモジュール読み込み時に基盤のカタログを load / activate 済みであり、
// ここでの追加ロードはその後に走る（`main.tsx` の `initI18n()` はさらに後。load はいつ行っても効く）。
//
// 🔴 **submodule の前進より先に本合成点が develop へ入る**ため、AST 側の新しい公開面
// （`aiStockTradingMessages` / `aiStockTradingBreadcrumbs`。AST の UI/UX 改善 PR で追加）は
// **無いかもしれない前提で読む**。名前付き import にすると旧 AST で tsc が落ち、AST の bump PR と
// 本 PR のどちらを先にマージしても赤になる（順序依存）。名前空間 import から任意項目として取り出せば
// どちらの順でも緑で、bump が来た時点で自然に有効になる。
// ［2026-09-12 / #1437 / AST#792］🔴 **bump 後も任意項目読みを維持する（名前付き import へ戻さない）。**
// AST は文言カタログの登録を画面の遅延チャンク側へ移し（AST/IADR-0340）、`aiStockTradingMessages` の
// 再公開を外した——合成点で同期に束ねると ja カタログ 25 kB が初期チャンクへ入るためである。
// 戻すと tsc が落ちる。`aiStockTradingBreadcrumbs` も同じ読み方のまま据え置く（IADR-0441 決定 3 の追記）。
const astOptionalSurface = aiStockTradingUnit as unknown as {
  aiStockTradingMessages?: Partial<Record<Locale, Messages>>;
  aiStockTradingBreadcrumbs?: readonly FeatureBreadcrumb[];
};
if (astOptionalSurface.aiStockTradingMessages) {
  registerUnitMessages(astOptionalSurface.aiStockTradingMessages);
}

/**
 * 型付きルートを持つユニットの合成（IADR-0124 決定 1）。
 *
 * **戻り値に型注釈を書かない。** `readonly AnyRoute[]` を注釈した瞬間にルート ID・パスの union が
 * 失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる（IADR-0124 §実測）。
 * ユニットを足すときはタプルのスプレッドを 1 行足す。
 */
export const createUnitRoutes = (shell: ShellRoute) =>
  [...createKnowledgeRoutes(shell), ...createAiStockTradingRoutes(shell)] as const;

/**
 * 本計画に属するユニットが公開するナビ項目（05_screens §共通シェル の 4 グループ付き）。
 * 型が `PlanNavItem` なので、グループの宣言漏れはここで `tsc` が落とす
 * （総称フォールバックを廃止したため、宣言漏れは「静かに消える」を意味する）。
 *
 * 🔴 **AST の項目はここに入らない。** 本計画に属さないユニットは `group` を宣言せず、
 * 下の `unitNavGroups` が機能名の見出しへ束ねる（IADR-0125 決定 9）。
 */
export const planNavItems: readonly PlanNavItem[] = [...knowledgeNavItems];

/**
 * ユニットが公開するパンくず宣言（05_screens §共通シェル「パンくず・権限バッジ」。#446）。
 *
 * **ナビ項目とは別の集合である**（SC-03 は左ナビに置かないがパンくずは持つ）。
 * 🔴 **AST はここに現れない** —— AST は現時点でパンくずを宣言していない（AST#414 の射程外）。
 * **合成点が代わりに書いてやらない** —— 画面の名前と親子関係はユニットしか知らない。
 * 宣言する用意ができれば、AST が `xxxBreadcrumbs` を公開してここへ 1 行足すだけで載る
 * （旧契約の時代と違い、**宣言面が無いという構造的な制約は無くなった**）。
 */
export const planBreadcrumbs: readonly FeatureBreadcrumb[] = [
  ...knowledgeBreadcrumbs,
  // AST のパンくず（「取引 / 設定」等）。AST が宣言を公開した時点で載る（上の任意項目の読み方と同じ）。
  ...(astOptionalSurface.aiStockTradingBreadcrumbs ?? []),
];

/**
 * 本計画に属さない可変機能ユニットの左ナビグループ
 * （05_screens §共通シェル ［2026-08-04 確定］。IADR-0125 決定 9）。
 *
 * 計画は「グループ名は**ユニットの機能名**とする（例: `ai-stock-trading` → 「株式自動売買」）。
 * **総称としての『その他』は使わない**」と定めた。ユニット自身（AST）は本リポジトリの計画に
 * 属さないため `group` を宣言せず、**機能名は合成点が与える**——合成点はユニットを知る
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
    items: aiStockTradingNavItems,
  },
];
