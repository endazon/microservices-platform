import { I18nProvider } from '@lingui/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import { configure, getConfig } from '@testing-library/dom';
import { act, render } from '@testing-library/react';
import { afterEach } from 'vitest';
import { AuthContext } from '@foundation/auth/AuthContext';
import type { AuthState } from '@foundation/auth/AuthContext';
import { i18n } from '@foundation/i18n';
import type { ShellRoute } from '@foundation/routing/shell';

// ADR-0031 / IADR-0124: 可変ユニットの画面テスト用ハーネス。
//
// ユニットの画面は型付きルート factory で公開されており、`useSearch({ from: '/_shell/...' })` /
// `useParams({ from: '/_shell/...' })` がルート ID のリテラルに依存する。したがってテストでも
// **実アプリと同じ id（`_shell`）を持つレイアウトルート**の下に載せる必要がある。
//
// 実アプリのルート木（`@foundation/routing/router`）をそのまま使わないのは、それが合成点
// （`@features`）＝他ユニットまで引き込み、ユニット単体のテストが他ユニットの存在に依存するためである
// （IADR-0056 の依存規則。可変ユニットは `@foundation` しか参照しない）。

// NFR, ADR-0031 / IADR-0134 決定 2: `configure()` は @testing-library/dom の**グローバル設定**を
// 書き換える。したがって `renderUnitRoute` の中で呼ぶだけでは「その 1 回の描画」ではなく
// **そのテストファイル全体**へ効いてしまい、横断 setup に置いたのと同じ弊害（「1 秒で落ちるべき
// 退行が 5 秒待って落ちる」）がファイル内で再現しうる。各テストの後に既定値へ戻すことで、
// **この入口を実際に使ったテストだけ**が延長を受けるようにする。
// （Vitest はファイル単位で環境を分離するため、他のテストファイルへは元々漏れない。）
const DEFAULT_ASYNC_UTIL_TIMEOUT = getConfig().asyncUtilTimeout;

afterEach(() => {
  configure({ asyncUtilTimeout: DEFAULT_ASYNC_UTIL_TIMEOUT });
});

/** ロールを持つ認証済みユーザーの AuthState（ADR-0032: 身元は /bff/auth/me の形。トークンは無い）。 */
export function authStateWithRoles(roles: readonly string[]): AuthState {
  return {
    user: { name: 'tester', subject: 'tester', roles: [...roles] },
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
}

export interface RenderUnitRouteOptions {
  /** 初期 URL（検索パラメータを含めてよい。例: "/search?q=経費"）。 */
  initialEntry: string;
  /** 認証済みユーザーのロール（RequireRole を通す画面で使う）。省略時も認証済みとして扱う。 */
  roles?: readonly string[];
}

/**
 * 検査用の QueryClient（#502 / IADR-0126 決定 5）。
 *
 * - **描画のたびに新しく作る**。使い回すと、あるテストが載せたキャッシュを次のテストが読む。
 * - `retry: false`: 本番の既定（`shouldRetryQuery`）は 5xx とネットワーク断を 1 度だけ再試行する。
 *   テストで異常系を検証するたびに再試行の往復を待つことになり、`apiFetch` の呼び出し回数を
 *   数えるテストも壊れる。再試行の判定そのものは `queryClient.test.ts` が固定する。
 * - `staleTime: 0` / `gcTime: 0`: キャッシュの持ち越しを作らない。
 */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
}

/**
 * ユニットのルート factory を、実アプリと同じ id を持つ検査用シェルの下で描画する。
 *
 * @param createRoutes ユニットが公開するルート factory（複数可）
 */
export async function renderUnitRoute(
  createRoutes: (shell: ShellRoute) => readonly AnyRoute[],
  { initialEntry, roles = [] }: RenderUnitRouteOptions,
) {
  // NFR, ADR-0031 / IADR-0134 決定 2: ガード（`RequireRole`）配下の画面は
  // `router.load()` の事前読み込みが効かない（`preloadRouteComponents` は
  // `route.options.component.preload` しか見ず、ガードで包んだ素の関数は `.preload` を持たない）。
  // よって**描画が始まってから動的 import が走り**、`findBy*` の待ち時間にその往復が乗る。
  // 既定の 1000 ms はカバレッジ計測を有効にすると足りないことがある（実測: `sc05-documents` /
  // `sc09-admin-abac` が落ちた。`pnpm run test` では再現せず `pnpm run test:coverage` でのみ再現）。
  //
  // **延長はこの入口を使ったテストに閉じる**——横断 setup（`platform/frontend/src/test/setup.ts`）に
  // 置くとガードと無関係なテスト（AST・`@platform/ui`・純関数）まで巻き込み、
  // 「1 秒で落ちるべき退行が 5 秒待って落ちる」経路を作る。閉じ込めは冒頭の `afterEach` が担う
  // （`configure()` はグローバル設定なので、戻さないとファイル内の他のテストまで巻き込む）。
  configure({ asyncUtilTimeout: 5000 });

  const testRoot = createRootRoute({ component: Outlet });
  // 実アプリの shellRoute と同じ id を持つ検査用レイアウト。ここだけが型の付け替えであり、
  // ユニット側から見た形（親ルート）は実アプリと同一である。
  const testShell = createRoute({
    getParentRoute: () => testRoot,
    id: '_shell',
    component: Outlet,
  }) as unknown as ShellRoute;

  // 検査用の木は「実行時に組み立てる」ため型付けを持たない（本番の型安全は router.tsx が担う）。
  type Composable = { addChildren: (children: readonly AnyRoute[]) => AnyRoute };
  const shell = (testShell as unknown as Composable).addChildren(createRoutes(testShell));
  const routeTree = (testRoot as unknown as Composable).addChildren([shell]);
  const router = createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [initialEntry] }),
  });

  // ADR-0031 / IADR-0125 決定 3 / IADR-0126 決定 5（#502）: 実アプリ（App.tsx）と同じ順序で
  // プロバイダを重ねる。画面は `<Trans>`（I18nProvider が要る）と TanStack Query を使うため、
  // ここで用意しないとユニット側のテストが本質でない wrapper を各ファイルで書くことになる
  // （#496 §未決事項 4 の引き受け）。
  const queryClient = createTestQueryClient();
  const result = render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        <AuthContext.Provider value={authStateWithRoles(roles)}>
          <RouterProvider router={router as never} />
        </AuthContext.Provider>
      </QueryClientProvider>
    </I18nProvider>,
  );
  // TanStack Router の初期描画は非同期（マッチの解決を待つ）。ここで待たないと、
  // 描画直後に getBy* を使う既存テストが空の DOM を見ることになる。
  await act(async () => {
    await router.load();
  });

  return { router, queryClient, ...result };
}
