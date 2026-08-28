import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-12, UC-09, FR-16, ADR-0024: MCP クライアント登録管理（05_screens: ルート /admin/mcp-clients）。
//
// 画面への到達は **platform-admin のみ**（05_screens §共通シェル「SC-09・SC-12・SC-17 =
// システム管理者」・§SC-12「システム管理者ロール限定」。運用者も不可）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// サーバ側 /bff/admin/mcp-clients も AdminOnly（BFF・後段の二重ゲート）。
//
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const McpClientManagementPage = lazyRouteComponent(
  () => import('../components/McpClientManagementPage'),
  'McpClientManagementPage',
);

export const createSc12McpClientsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/mcp-clients',
    // NFR, ADR-0031 / IADR-0134: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクを取得しない（存在秘匿。IADR-0009）。
    // 反面 router.load() の事前読み込みが効かず描画時に suspend するため、
    // このルートには Suspense 境界が要る（無いと最上位まで遡り共通シェルごと消える）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin]}>
          <McpClientManagementPage />
        </RequireRole>
      );
    },
  });

// 05_screens §共通シェル: 左ナビ「管理」グループの「MCP管理」（hi-fi モックの左レール準拠）。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc12McpClientsNav: PlanNavItem = {
  id: 'sc12-mcp-clients',
  label: msg`MCP管理`,
  to: '/admin/mcp-clients',
  group: 'admin',
  requiresAnyRole: [PlatformRole.Admin],
};

// 05_screens §共通シェル / #446: パンくず `ホーム / 管理 / ABAC設定 / MCPクライアント`（crumb 実測）。
export const sc12McpClientsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/admin/mcp-clients',
  group: 'admin',
  parents: [{ label: msg`ABAC設定`, to: '/admin/abac' }],
  label: msg`MCPクライアント`,
  requiresAnyRole: [PlatformRole.Admin],
};
