import { I18nProvider } from '@lingui/react';
import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { AuthProvider } from '@foundation/auth/AuthProvider';
import { ErrorBoundary } from '@foundation/ui/ErrorBoundary';
import { queryClient } from '@foundation/api/queryClient';
import { router } from '@foundation/routing/router';
import { i18n } from '@foundation/i18n';

// Issue #126: 合成ルート。エラーバウンダリ＋サーバー状態キャッシュ＋認証プロバイダ＋ルータを束ねる。
// ADR-0031 / IADR-0121: サーバー状態は TanStack Query に一元化する（グローバルストア＝Redux は持たない）。
// QueryClientProvider は AuthProvider の外側に置く——認証状態が変わってもキャッシュの実体は同一であり、
// ログアウト時の破棄は認証側から明示的に行う（第 3 段 / #439）。
// ADR-0031 / IADR-0124: ルータは TanStack Router（移行第 2 段 / #490 で react-router-dom から差し替え）。
// AuthProvider はルータの外側——ルート要素（RequireAuth / RequireRole）が認証状態を読むためである。
// ADR-0031 / IADR-0125 決定 3: I18nProvider は最も外側に置く。
//
// foundation 自身は `<Trans>` を使わず `i18n._(msg\`…\`)` で文言を解決する（プロバイダに依存しない）。
// これは意図的である——foundation のコンポーネントは Layout・NotFound・LoginPage のように
// **多数の単体テストが素で描画する**ため、プロバイダ必須にすると 30 件超のテストが
// 「本質でない wrapper の有無」で割れる。foundation が出すのは素の文字列だけであり、
// リッチテキスト（要素の埋め込み）や複数形は使わないので `msg` で足りる。
//
// それでもここにプロバイダを置くのは、**画面側（#452）が `<Trans>` を使えるようにする**ためである。
export function App() {
  return (
    <I18nProvider i18n={i18n}>
      <ErrorBoundary>
        <QueryClientProvider client={queryClient}>
          <AuthProvider>
            <RouterProvider router={router} />
          </AuthProvider>
        </QueryClientProvider>
      </ErrorBoundary>
    </I18nProvider>
  );
}
