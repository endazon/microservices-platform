import { createRouter } from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import { rootRoute, loginRoute, shellRoute, homeRedirectRoute, catchAllRoute } from './shell';
import { createLegacyRoutes } from './featureRegistry';
import { registerNavItems, registerUnitNavGroups } from './nav';
import { registerBreadcrumbs } from './breadcrumbs';
import {
  createUnitRoutes,
  planNavItems,
  planBreadcrumbs,
  legacyUnitFeatures,
  unitNavGroups,
} from '@features/index';

// ADR-0031 / IADR-0124: ルート木の組み立て。
//
// 型安全の要は「型付きルートだけで addChildren を済ませる」こと。ここへ AnyRoute を 1 つでも
// 混ぜると、ルート ID・パスの union も検索パラメータの型も**静かに**失われる（IADR-0124 §実測）。
// 旧契約（AST）のルートは型付けを済ませた後で children へ足す。
// `/`（アプリホストの責務。IADR-0124 決定 6）と未知パスの受け皿（同 決定 8）は
// ユニットではなく shell.tsx が持つ。catchAllRoute はスプラットのため最後に置く。
const shellWithUnits = shellRoute.addChildren([
  homeRedirectRoute,
  ...createUnitRoutes(shellRoute),
  catchAllRoute,
] as const);

/**
 * 旧契約ユニットのルートを実行時にだけ接ぎ木する（IADR-0124 決定 2）。
 * 型には現れないため、これらの画面は `<Link to>` の union に入らない（受け入れ済みの制約）。
 */
function appendLegacyRoutes(routes: AnyRoute[]): void {
  if (routes.length === 0) return;
  // 型付きの children（タプル）へ「型を持たないルート」を足す操作であり、型の上では表現できない。
  // ここが本ファイルで唯一の型消去であり、これを型付きの配列側へ持ち込むと union が壊れる。
  const parent = shellWithUnits as unknown as { children?: AnyRoute[] };
  parent.children = [...(parent.children ?? []), ...routes];
}
appendLegacyRoutes(createLegacyRoutes(shellRoute, legacyUnitFeatures));

// IADR-0124 決定 1: 合成点を知るのはこのモジュールだけ。共通シェル（foundation/ui/Layout）は
// 登録済みのナビを読むだけで、可変ユニットを参照しない。
registerNavItems(planNavItems);
// 05_screens §共通シェル ［2026-08-04 確定］: 本計画に属さないユニットは**機能名**のグループへ束ねる
// （総称の「その他」は使わない）。並びは計画の 4 グループの後（nav.ts の navGroups）。
registerUnitNavGroups(unitNavGroups);
// 05_screens §共通シェル「パンくず・権限バッジ」（#446）: パンくずは**ナビとは別の登録面**である
// （SC-03 はナビ項目を持たないがパンくずは持つ）。登録するのはここ 1 か所だけ。
// 🔴 **旧契約ユニット（AST）はパンくずを持たない** —— `FeatureModule` に宣言面が無く、
// 本リポジトリからは変更できない（IADR-0120）。総称のフォールバックは作らない
// （左ナビの「その他」を作らないのと同じ理由。何の画面か分からない段を出すほうが害である）。
registerBreadcrumbs(planBreadcrumbs);

export const routeTree = rootRoute.addChildren([loginRoute, shellWithUnits]);

export const router = createRouter({ routeTree });

// IADR-0124 決定 4: 型登録は `@tanstack/react-router`（再エクスポート側）ではなく、
// Register インターフェースの**宣言元**である `@tanstack/router-core` へ行う。
// 宛先を誤ると型エラーは出ないまま useSearch / useParams / Link の型が全て緩む。
declare module '@tanstack/router-core' {
  interface Register {
    router: typeof router;
  }
}
