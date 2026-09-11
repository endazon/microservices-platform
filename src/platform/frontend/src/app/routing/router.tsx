import { createRouter } from '@tanstack/react-router';
import { rootRoute, loginRoute, shellRoute, homeRedirectRoute, catchAllRoute } from './shell';
import { registerNavItems, registerUnitNavGroups } from './nav';
import { registerBreadcrumbs } from './breadcrumbs';
import { RouteErrorComponent, RoutePendingComponent } from './routeStates';
import { createUnitRoutes, planNavItems, planBreadcrumbs, unitNavGroups } from '@features/index';

// ADR-0031 / IADR-0124: ルート木の組み立て。
//
// 型安全の要は「型付きルートだけで addChildren を済ませる」こと。ここへ AnyRoute を 1 つでも
// 混ぜると、ルート ID・パスの union も検索パラメータの型も**静かに**失われる（IADR-0124 §実測）。
// `/`（アプリホストの責務。IADR-0124 決定 6）と未知パスの受け皿（同 決定 8）は
// ユニットではなく shell.tsx が持つ。catchAllRoute はスプラットのため最後に置く。
//
// ［2026-09-03 / AST#414］**旧契約ユニットの実行時接ぎ木（`appendLegacyRoutes`）は消えた。**
// あれは本ファイルで唯一の型消去であり、`src/ai-stock-trading` が旧契約
// （`FeatureModule { id, routes, nav }`）のままだったことだけを理由に存在していた（IADR-0124 決定 2）。
// AST が型付きルート factory を公開したため、**全ユニットのルートがこのタプル 1 本に載る**
// ——AST の 3 画面も `<Link to>` の union に入る。
const shellWithUnits = shellRoute.addChildren([
  homeRedirectRoute,
  ...createUnitRoutes(shellRoute),
  catchAllRoute,
] as const);

// IADR-0124 決定 1: 合成点を知るのはこのモジュールだけ。共通シェル（app/Layout）は
// 登録済みのナビを読むだけで、可変ユニットを参照しない。
registerNavItems(planNavItems);
// 05_screens §共通シェル ［2026-08-04 確定］: 本計画に属さないユニットは**機能名**のグループへ束ねる
// （総称の「その他」は使わない）。並びは計画の 4 グループの後（nav.ts の navGroups）。
registerUnitNavGroups(unitNavGroups);
// 05_screens §共通シェル「パンくず・権限バッジ」（#446）: パンくずは**ナビとは別の登録面**である
// （SC-03 はナビ項目を持たないがパンくずは持つ）。登録するのはここ 1 か所だけ。
// 🔴 **AST の 3 画面はパンくずを持たない** —— AST がまだ宣言していないためである
// （AST#414 で宣言面そのものは使えるようになった。宣言するかどうかはユニットの判断）。
// 総称のフォールバックは作らない（左ナビの「その他」を作らないのと同じ理由。
// 何の画面か分からない段を出すほうが害である）。
registerBreadcrumbs(planBreadcrumbs);

export const routeTree = rootRoute.addChildren([loginRoute, shellWithUnits]);

export const router = createRouter({
  routeTree,
  // ［2026-09-12 / UI/UX 改善 裁定 6］**画面単位のエラー境界・待ち表示・遷移時の先読み。**
  //
  // `defaultErrorComponent` / `defaultPendingComponent` は**マッチしたルートの位置**
  // （＝共通シェルの本文）だけを差し替える。アプリ全体を差し替える `App.tsx` の
  // `ErrorBoundary` とは役割が違い、**両方要る**（ルータの外の例外はルータでは捕まらない）。
  // 中身は `routeStates.tsx`——`createRouter` の呼び出しに JSX を混ぜないためである。
  defaultErrorComponent: RouteErrorComponent,
  defaultPendingComponent: RoutePendingComponent,
  // **リンクに触れた（hover / focus）時点で次の画面を読み始める。** 遷移の体感はこれで決まる。
  // `'render'`（描画と同時に全リンクを先読み）は採らない——左レールには常時 10 数本の
  // リンクが並んでおり、開いただけで全画面分の取得が走る。
  // `defaultPreloadStaleTime` は既定（30 秒）のまま——先読みの結果をどれだけ信じるかは
  // 画面ごとの `staleTime` が決めるべき値であり、ここで一律に伸ばすと古い内容が出る。
  defaultPreload: 'intent',
});

// IADR-0124 決定 4: 型登録は `@tanstack/react-router`（再エクスポート側）ではなく、
// Register インターフェースの**宣言元**である `@tanstack/router-core` へ行う。
// 宛先を誤ると型エラーは出ないまま useSearch / useParams / Link の型が全て緩む。
declare module '@tanstack/router-core' {
  interface Register {
    router: typeof router;
  }
}
