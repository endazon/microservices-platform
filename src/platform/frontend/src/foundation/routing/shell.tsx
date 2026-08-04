import { createRootRoute, createRoute, Outlet } from '@tanstack/react-router';
import { Layout } from '@foundation/ui/Layout';
import { NotFound } from '@foundation/ui/NotFound';
import { RequireAuth } from '@foundation/auth/RequireAuth';
import { LoginPage } from '@foundation/auth/LoginPage';
import { CallbackPage } from '@foundation/auth/CallbackPage';

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
  // 外部由来の値なので、SPA 内部の絶対パスでなければ捨てる（オープンリダイレクト対策）。
  validateSearch: (raw: Record<string, unknown>): { from?: string } => {
    const from = raw.from;
    return typeof from === 'string' && from.startsWith('/') && !from.startsWith('//')
      ? { from }
      : {};
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
  component: function ShellRouteComponent() {
    return (
      <RequireAuth>
        <Layout />
      </RequireAuth>
    );
  },
});

/**
 * 可変機能ユニットがルートを生やす親（IADR-0124 決定 1）。
 * ユニットは platform のルート木を import せず、この型の値を引数で受け取る。
 */
export type ShellRoute = typeof shellRoute;
