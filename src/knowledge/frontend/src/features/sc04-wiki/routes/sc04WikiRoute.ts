import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { validateWikiSearch } from '../types/wikiSearch';

// SC-04, UC-07, FR-13, ADR-0073 決定 2 (#1200): Wiki 閲覧画面（05_screens §SC-04 §ルート: `/wiki`（基盤 SPA））。
// ページツリー・本文・検索を `/bff/wiki/*` 経由で取得して SPA が描く。従前の「実体は別ホストの Wiki.js で
// 本 feature は遷移導線のみ」は ADR-0073 が撤回した（計画 §ルートの ［2026-09-03 是正］）。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
// 本文の sanitize（dompurify）もこのチャンクにだけ載る。
const WikiBrowsePage = lazyRouteComponent(
  () => import('../components/WikiBrowsePage'),
  'WikiBrowsePage',
);

export const createSc04WikiRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/wiki',
    // IADR-0124 決定 3 / IADR-0365 決定 4: 開いているページ（`?page=` / `?doc=`）と検索語（`?q=`）は
    // URL が単一情報源。値の正規化は `types/wikiSearch.ts`。
    validateSearch: validateWikiSearch,
    component: WikiBrowsePage,
  });

export const sc04WikiNav: PlanNavItem = {
  id: 'sc04-wiki',
  label: 'Wiki',
  to: '/wiki',
  group: 'user',
};

// 05_screens §共通シェル / #446: パンくず `ホーム / Wiki / <ページの題名>`。
//
// モックの crumb は `ホーム / Wiki / 経理 / 経費精算規程` だが、台帳のページは平坦（`doc/<id>`）で
// 中間の段（`経理`）に当たる階層を持たない。**葉（ページの題名）は画面が `useBreadcrumbLeaf` で渡す**
// （SC-03 の文書タイトルと同じ作法。取得前は描かない）。
// 🔴 `label` を持たせない —— `breadcrumbTrail()` は `label ?? leaf` で自画面の段を決めるため、
// `label: 'Wiki'` を置くと葉（題名）が**一度も描かれない**（稼働環境で実測。#1200）。
// 「Wiki」の段は SC-03 の「検索結果」と同じく**親の段**（`/wiki` 自身へのリンク）として置き、
// ページを開いていないときは `ホーム / Wiki`、開いたら `ホーム / Wiki / <題名>` になる。
// ページを開いていないとき「Wiki」は現在地であり、`breadcrumbTrail()` が自ルートを指す末尾の
// 親の段を現在地へ格下げする（リンクにしない。Layout.test.tsx「現在地はリンクではない」）。
// 表示名はナビと同じく固有名詞のリテラル（翻訳カタログへ載せない）。
export const sc04WikiBreadcrumb: FeatureBreadcrumb = {
  routePath: '/wiki',
  group: 'user',
  parents: [{ label: 'Wiki', to: '/wiki' }],
};
