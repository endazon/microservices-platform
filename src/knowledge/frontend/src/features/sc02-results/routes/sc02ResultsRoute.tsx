import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-02, UC-01, FR-03/FR-05: 検索結果一覧（05_screens: ルート /search?q=）。認証済みユーザー向け。
// ABAC はサーバ側（/bff/search の deny-by-default）で適用され、権限外文書は結果に現れない。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SearchResultsPage = lazyRouteComponent(
  () => import('../components/SearchResultsPage'),
  'SearchResultsPage',
);

export const createSc02ResultsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/search',
    // SC-02, IADR-0124: `?q=` を型付きの検索パラメータとして受ける（ADR-0031 が TanStack Router を
    // 採った理由そのもの）。URL は外部由来なので、文字列でない・欠落した値は空文字へ正規化する。
    // IADR-0126 決定 3: この値が検索語の**単一情報源**である。
    validateSearch: (raw: Record<string, unknown>): { q: string } => ({
      q: typeof raw.q === 'string' ? raw.q : '',
    }),
    component: SearchResultsPage,
  });

// 05_screens §共通シェル: 左ナビ「利用者」グループの「結果一覧」（hi-fi モックの左レール準拠）。
// 表示名を MessageDescriptor で持つ理由は sc01-search/index.tsx のコメントを参照。
export const sc02ResultsNav: PlanNavItem = {
  id: 'sc02-results',
  label: msg`結果一覧`,
  to: '/search',
  group: 'user',
};
