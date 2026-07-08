import type { RouteObject } from 'react-router-dom';

// Issue #126: feature（画面）モジュールの契約。各 SC-xx は FeatureModule を 1 つ公開し、
// features/index.ts へ登録するだけで認証済みレイアウト配下にマウントされる（骨組みへの追加が疎結合）。
export interface FeatureModule {
  /** 一意な識別子（例: "home", "sc01-search"）。 */
  id: string;
  /** Layout の Outlet 配下に載る子ルート群（path は "/" 起点の相対）。 */
  routes: RouteObject[];
}
