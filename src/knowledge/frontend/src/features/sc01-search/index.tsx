import { createRoute, redirect } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { SearchChatPage } from './SearchChatPage';

// SC-01, UC-01, FR-03/FR-04: 検索／チャット質問画面（本システムの主入口。05_screens: ルート /ask）。
// 認証済みユーザー向け（ロール限定なし）。ABAC は後段（BFF/検索/AI）が narrowing・deny-by-default で適用。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc01SearchRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/ask',
    component: SearchChatPage,
  });

// IADR-0124 決定 6: 計画の画面一覧（SC-01〜21）に home に相当する画面は存在せず、SC-01 が
// 「本システムの主入口」と定義されている。ルート直下は SC-01 へ送る（旧 home 画面は削除した）。
export const createHomeRedirectRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/',
    beforeLoad: () => {
      throw redirect({ to: '/ask', replace: true });
    },
  });

export const sc01SearchNav: NavItem = {
  id: 'sc01-search',
  label: '検索 / AI質問',
  to: '/ask',
  group: 'user',
};
