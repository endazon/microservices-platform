import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { AuthProvider } from '@foundation/auth/AuthProvider';
import { ErrorBoundary } from '@foundation/ui/ErrorBoundary';
import { queryClient } from '@foundation/api/queryClient';
import { router } from '@foundation/routing/router';

// Issue #126: 合成ルート。エラーバウンダリ＋サーバー状態キャッシュ＋認証プロバイダ＋ルータを束ねる。
// ADR-0031 / IADR-0121: サーバー状態は TanStack Query に一元化する（グローバルストア＝Redux は持たない）。
// QueryClientProvider は AuthProvider の外側に置く——認証状態が変わってもキャッシュの実体は同一であり、
// ログアウト時の破棄は認証側から明示的に行う（第 3 段 / #439）。
// ルータは移行第 2 段で TanStack Router へ置き換える（IADR-0121 決定 1）。
export function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <RouterProvider router={router} />
        </AuthProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}
