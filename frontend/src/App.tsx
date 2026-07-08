import { RouterProvider } from 'react-router-dom';
import { AuthProvider } from '@foundation/auth/AuthProvider';
import { ErrorBoundary } from '@foundation/ui/ErrorBoundary';
import { router } from '@foundation/routing/router';

// Issue #126: 合成ルート。認証プロバイダ＋エラーバウンダリ＋ルータを束ねる。
export function App() {
  return (
    <ErrorBoundary>
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>
    </ErrorBoundary>
  );
}
