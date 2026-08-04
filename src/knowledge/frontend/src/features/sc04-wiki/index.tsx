import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { NavItem } from '@foundation/routing/featureRegistry';
import { WikiAccessPage } from './WikiAccessPage';

// SC-04, UC-07, FR-13: Wiki 閲覧導線。実体は Wiki.js（別ホスト・ABAC ゲートウェイ経由・SSO）であり、
// 本 feature は SPA 側の遷移導線のみを提供する（05_screens は SC-04 に SPA ルートを定義していない）。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

export const createSc04WikiRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/wiki',
    component: WikiAccessPage,
  });

export const sc04WikiNav: NavItem = {
  id: 'sc04-wiki',
  label: 'Wiki',
  to: '/wiki',
  group: 'user',
};
