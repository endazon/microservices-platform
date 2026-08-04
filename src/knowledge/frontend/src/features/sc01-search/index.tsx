import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { SearchChatPage } from './SearchChatPage';

// SC-01, UC-01, FR-03/FR-04: 検索／チャット質問画面（本システムの主入口。05_screens: ルート /ask）。
// 認証済みユーザー向け（ロール限定なし）。ABAC は後段（BFF/検索/AI）が narrowing・deny-by-default で適用。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。
//
// ルート直下（`/`）から本画面へのリダイレクトは **platform 側**（`@foundation/routing/shell` の
// `homeRedirectRoute`）が持つ。`/` の存在はアプリホストの責務であり、可変ユニットを外したときに
// 消えてはならないためである（IADR-0124 決定 6）。

export const createSc01SearchRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/ask',
    component: SearchChatPage,
  });

export const sc01SearchNav: NavItem = {
  id: 'sc01-search',
  label: '検索 / AI質問',
  to: '/ask',
  group: 'user',
};
