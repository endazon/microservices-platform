import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { PlatformRole } from '@foundation/auth/roles';

// SC-22, FR-05, ADR-0095, ADR-0042 決定 2, IADR-0453 決定 1: 秘密情報・接続設定の管理
// （05_screens: ルート /admin/secrets）。
//
// 画面への到達は **platform-admin または platform-operator**（計画 §SC-22「運用者・システム管理者ロール限定」）。
// 権限外は RequireRole が NotFound を描画して画面の存在を示さない（存在秘匿。IADR-0009 / IADR-0035）。
// サーバ側 /bff/secrets も同じ 2 ロールに限る（`SecretItemWriter`。未認証 401・権限外 403）。
//
// 認可の実効境界はサーバ側であり、UI は表示制御と存在秘匿のためだけに用いる。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SecretItemManagementPage = lazyRouteComponent(
  () => import('../components/SecretItemManagementPage'),
  'SecretItemManagementPage',
);

export const createSc22SecretsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/admin/secrets',
    // NFR, ADR-0031 / IADR-0134: ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。
    // ガードが先に評価されるため、権限外の利用者は画面チャンクも一覧も取得しない（存在秘匿。IADR-0009）。
    wrapInSuspense: true,
    component: function GuardedRoute() {
      return (
        <RequireRole anyOf={[PlatformRole.Admin, PlatformRole.Operator]}>
          <SecretItemManagementPage />
        </RequireRole>
      );
    },
  });

// 05_screens §SC-22「共通シェル: 適用する（左ナビ『運用』グループ）」。
// 表示名を MessageDescriptor で持つ理由は featureRegistry.ts（NavLabel）のコメントを参照。
export const sc22SecretsNav: PlanNavItem = {
  id: 'sc22-secrets',
  label: msg`秘密情報・接続設定の管理`,
  to: '/admin/secrets',
  group: 'ops',
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};

// 05_screens §共通シェル: パンくず `ホーム / 運用 / 秘密情報・接続設定の管理`。
export const sc22SecretsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/admin/secrets',
  group: 'ops',
  label: msg`秘密情報・接続設定の管理`,
  requiresAnyRole: [PlatformRole.Admin, PlatformRole.Operator],
};
