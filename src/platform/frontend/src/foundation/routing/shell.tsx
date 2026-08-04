import { createRootRoute, createRoute, Outlet, redirect } from '@tanstack/react-router';
import { Layout } from '@foundation/ui/Layout';
import { NotFound } from '@foundation/ui/NotFound';
import { RequireAuth } from '@foundation/auth/RequireAuth';
import { LoginPage } from '@foundation/auth/LoginPage';
import { CallbackPage } from '@foundation/auth/CallbackPage';
import { toInternalPath } from '@foundation/auth/safeRedirect';
// `/` の遷移先。CallbackPage も使うため、循環 import を避けて単独モジュールに置く（IADR-0124 決定 6）。
import { ENTRY_ROUTE_PATH } from './entryPath';

// ADR-0031 / IADR-0124: ルート木の「骨格」。可変機能ユニットのルートはここには現れない
// （合成点 platform/frontend/src/features/index.ts が束ね、router.tsx が接ぎ木する）。
// 本ファイルが features を参照しないことが、IADR-0056 決定 3（platform → 可変ユニット禁止）の担保である。

export const rootRoute = createRootRoute({
  component: Outlet,
  // IADR-0009: 存在秘匿。不在も権限による秘匿も同じ画面で応答する。
  notFoundComponent: NotFound,
});

// 認証導線。計画のルート表（05_screens §共通シェル）には無い SPA 内部のパスであり、
// 第 3 段（#439 / ADR-0032 の BFF セッション方式）で見直す。
export const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  // IADR-0124 決定 3: 遷移元を型付き検索パラメータで受ける（RequireAuth が付ける）。
  // IADR-0124 決定 9: `from` は URL に載る＝利用者が書き換えられる外部由来の値なので、
  // この SPA 内部のパスとして解決できるものだけを通す（オープンリダイレクト対策）。
  // 判定は toInternalPath に集約する（同じ条件を 2 か所へ書かない）。
  validateSearch: (raw: Record<string, unknown>): { from?: string } => {
    const from = toInternalPath(raw.from);
    return from === null ? {} : { from };
  },
  component: LoginPage,
});

export const callbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/callback',
  component: CallbackPage,
});

// IADR-0124 決定 1: 認証済み領域の共通シェル。path を持たない「レイアウトルート」であり、
// 配下ルートの ID は `/_shell/<path>` になる（画面が useSearch({ from }) へ渡すリテラルの出所）。
export const shellRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: '_shell',
  // シェル配下から notFound() が投げられた場合の描画（下の catchAllRoute とは別経路）。
  notFoundComponent: NotFound,
  component: function ShellRouteComponent() {
    return (
      <RequireAuth>
        <Layout />
      </RequireAuth>
    );
  },
});

// IADR-0124 決定 6: `/` は主入口へ送る。beforeLoad で投げるため、描画前にリダイレクトされる。
export const homeRedirectRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: ENTRY_ROUTE_PATH, replace: true });
  },
});

/**
 * 未知パスの受け皿（IADR-0009 / IADR-0124 決定 8）。**共通シェルの配下**に置くことが要点である。
 *
 * `notFoundComponent` を rootRoute に置くだけでは、未知パスはどのルートにもマッチせず
 * ルート木の**最上位**で解決されるため、共通シェル（ナビ・ヘッダ）の**外側**に素の NotFound が出る。
 * 一方で権限による秘匿（`RequireRole` → `NotFound`）はシェルの**内側**に出る。
 * この差は「シェルが出るかどうか」で資源の存在を推測させ、存在秘匿（IADR-0009）の趣旨に反する。
 * 移行前の実装が catch-all（`{ path: '*' }`）を `RequireAuth` ＋ `Layout` 配下に置いていたのと同じ配置であり、
 * 未認証時に `/login` へ誘導される挙動も維持される。
 *
 * 静的パスの方がスプラットより優先されるため、実在するルートを横取りしない
 * （`/login` `/callback` `/` および全ユニットのルートが先にマッチする。`router.test.ts` が固定する）。
 */
export const catchAllRoute = createRoute({
  getParentRoute: () => shellRoute,
  path: '$',
  component: NotFound,
});

/**
 * 可変機能ユニットがルートを生やす親（IADR-0124 決定 1）。
 * ユニットは platform のルート木を import せず、この型の値を引数で受け取る。
 */
export type ShellRoute = typeof shellRoute;
