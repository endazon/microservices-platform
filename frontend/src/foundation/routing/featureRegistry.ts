import type { RouteObject } from 'react-router-dom';

// Issue #136 / IADR-0034: 共通ナビへ出すメニュー項目。権限外には表示しない（存在秘匿の UI 表現）。
export interface FeatureNav {
  /** ナビ表示名（例: "運用"）。 */
  label: string;
  /** 遷移先パス（例: "/ops"）。 */
  to: string;
  /** 表示に必要なロール（いずれか一致で表示）。省略時は認証済み全員に表示する。 */
  requiresAnyRole?: string[];
}

// Issue #126: feature（画面）モジュールの契約。各 SC-xx は FeatureModule を 1 つ公開し、
// features/index.ts へ登録するだけで認証済みレイアウト配下にマウントされる（骨組みへの追加が疎結合）。
export interface FeatureModule {
  /** 一意な識別子（例: "home", "sc01-search"）。 */
  id: string;
  /** Layout の Outlet 配下に載る子ルート群（path は "/" 起点の相対）。 */
  routes: RouteObject[];
  /** 共通ナビへ出す項目（任意）。権限外には表示しない（Issue #136 / IADR-0034）。 */
  nav?: FeatureNav;
}
