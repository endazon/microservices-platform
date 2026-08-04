import { createRoute } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import { DocumentDetailPage } from './DocumentDetailPage';

// SC-03, UC-01/UC-07, FR-06: 文書詳細／プレビュー（05_screens: ルート /docs/:id）。
// 認証済みユーザー向け（ロール限定なし）。検索結果一覧（SC-02）・文書管理（SC-05）から遷移する。
// ナビには出さない（一覧・検索からの遷移で到達する）。ABAC はサーバ側（BFF）で適用され、権限外は 404 秘匿。
// ADR-0031 / IADR-0124 決定 1: パスパラメータは TanStack の `$id` 表記。`useParams({ from })` で型が付く。

export const createSc03DocumentRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/docs/$id',
    component: DocumentDetailPage,
  });
