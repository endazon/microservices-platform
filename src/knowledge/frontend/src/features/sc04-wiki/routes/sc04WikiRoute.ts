import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-04, UC-07, FR-13: Wiki 閲覧導線。実体は Wiki.js（別ホスト・ABAC ゲートウェイ経由・SSO）であり、
// 本 feature は SPA 側の遷移導線のみを提供する（05_screens は SC-04 に SPA ルートを定義していない）。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const WikiAccessPage = lazyRouteComponent(
  () => import('../components/WikiAccessPage'),
  'WikiAccessPage',
);

export const createSc04WikiRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/wiki',
    component: WikiAccessPage,
  });

export const sc04WikiNav: PlanNavItem = {
  id: 'sc04-wiki',
  label: 'Wiki',
  to: '/wiki',
  group: 'user',
};

// 05_screens §共通シェル / #446: パンくず `ホーム / Wiki`。
//
// モックの crumb は `ホーム / Wiki / 経理 / 経費精算規程` だが、**後半 2 段は Wiki.js（別ホスト）の
// ページ階層**である。SPA 側の `/wiki` は遷移導線しか持たない（計画は SC-04 に SPA ルートを
// 与えていない）ため、他ホストのページツリーを知らない。`ホーム / Wiki` までを宣言する。
// 表示名はナビと同じく固有名詞のリテラル（翻訳カタログへ載せない）。
export const sc04WikiBreadcrumb: FeatureBreadcrumb = {
  routePath: '/wiki',
  group: 'user',
  label: 'Wiki',
};
