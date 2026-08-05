import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細／プレビュー（05_screens: ルート /docs/:id）。
// 認証済みユーザー向け（ロール限定なし）。SC-01 の出典・SC-02 の一覧・SC-05 の文書管理から遷移する。
// ABAC はサーバ側（BFF）で適用され、権限外は 404 秘匿。
//
// **ナビには出さない。** 05_screens §共通シェル は「利用者」グループに SC-03 を含めているが、
// 本画面のルートは文書 ID を必須とする（`/docs/$id`）ため、ID を持たないナビ項目からは到達できない
// （`/docs/` は未知パスとして 404 になる）。計画のグループ分けは画面の所属を示すものであり、
// 各画面が単独のナビ入口を持つことまでは要求していない（画面仕様書 §左ナビに出さない理由）。
//
// ADR-0031 / IADR-0124 決定 1: パスパラメータは TanStack の `$id` 表記。`useParams({ from })` で型が付く。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const DocumentDetailPage = lazyRouteComponent(() => import('./DocumentDetailPage'), 'DocumentDetailPage');

export const createSc03DocumentRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/docs/$id',
    component: DocumentDetailPage,
  });
