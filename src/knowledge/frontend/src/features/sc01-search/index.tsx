import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-01, UC-01, FR-03/FR-04: 検索／チャット質問画面（本システムの主入口。05_screens: ルート /ask）。
// 認証済みユーザー向け（ロール限定なし）。ABAC は後段（BFF/検索/AI）が narrowing・deny-by-default で適用。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。
//
// ルート直下（`/`）から本画面へのリダイレクトは **platform 側**（`@foundation/routing/shell` の
// `homeRedirectRoute`）が持つ。`/` の存在はアプリホストの責務であり、可変ユニットを外したときに
// 消えてはならないためである（IADR-0124 決定 6）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SearchChatPage = lazyRouteComponent(() => import('./SearchChatPage'), 'SearchChatPage');

export const createSc01SearchRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/ask',
    component: SearchChatPage,
  });

// 05_screens §共通シェル: 左ナビ「利用者」グループの「検索・質問」（hi-fi モックの左レール準拠）。
// ADR-0031 / IADR-0125 決定 6（#502 で拡張）: 表示名は **MessageDescriptor** で持ち、
// 解決は描画時（`navGroups()`）に行う——ここは**モジュール初期化時**に評価されるため、
// 翻訳済みの文字列で持つとロケール切替に追随しない。
export const sc01SearchNav: PlanNavItem = {
  id: 'sc01-search',
  label: msg`検索・質問`,
  to: '/ask',
  group: 'user',
};
