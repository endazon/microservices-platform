import { createBrowserRouter } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { Layout } from '@foundation/ui/Layout';
import { NotFound } from '@foundation/ui/NotFound';
import { RequireAuth } from '@foundation/auth/RequireAuth';
import { LoginPage } from '@foundation/auth/LoginPage';
import { CallbackPage } from '@foundation/auth/CallbackPage';
import { features } from '@features/index';

// Issue #126: ルーティング組み立て。features の子ルートを認証ガード + 共通レイアウト配下へ束ねる。
export function buildRoutes(): RouteObject[] {
  const featureChildren = features.flatMap((f) => f.routes);
  return [
    { path: '/login', element: <LoginPage /> },
    { path: '/callback', element: <CallbackPage /> },
    {
      path: '/',
      element: (
        <RequireAuth>
          <Layout />
        </RequireAuth>
      ),
      children: [...featureChildren, { path: '*', element: <NotFound /> }],
    },
  ];
}

export const router = createBrowserRouter(buildRoutes());
