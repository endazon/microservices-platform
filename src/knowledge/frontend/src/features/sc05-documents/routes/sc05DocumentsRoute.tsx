import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-05, UC-03, FR-06/FR-09: 文書管理（05_screens: ルート /admin/documents）。管理系画面のため
// platform-admin/operator 限定（IADR-0039 / IADR-0041）。詳細・版履歴は SC-03（/docs/$id）へ遷移する。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009）。
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const DocumentManagementPage = lazyRouteComponent(
  () => import('../components/DocumentManagementPage'),
  'DocumentManagementPage',
);

export const createSc05DocumentsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/documents',
    // NFR, ADR-0031 / IADR-0134: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
    // 反面 router.load() の事前読み込み（preloadRouteComponents）が効かず描画時に suspend するため、
    // このルートには Suspense 境界が要る（無いと最上位まで遡り共通シェルごと消える）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <DocumentManagementPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「管理」グループの「文書管理」（hi-fi モックの左レール準拠）。
export const sc05DocumentsNav: PlanNavItem = {
  id: 'sc05-documents',
  label: msg`文書管理`,
  to: '/admin/documents',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};

// 05_screens §共通シェル / #446: パンくず `ホーム / 管理 / 文書管理`（crumb 実測）。
// `requiresAnyRole` は**上のナビ項目およびルートの `RequireRole anyOf` と同じ値**にする
// （権限外でパンくずを描くと、NotFound の外側から画面の存在が読める。IADR-0009）。
export const sc05DocumentsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/admin/documents',
  group: 'admin',
  label: msg`文書管理`,
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
