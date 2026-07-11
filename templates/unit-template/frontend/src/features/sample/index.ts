// テンプレート: ユニットのサンプル feature。
// FeatureModule 型（src/platform/frontend/src/foundation/routing/featureRegistry.ts）は
//   { id: string; routes: RouteObject[]; nav?: { label; to; requiresAnyRole? } }
// で構成する。routes は Layout の Outlet 配下に載る子ルート群、nav は共通ナビ項目（任意・権限外は非表示）。
// 依存規則: 可変ユニットは @foundation のみ参照可（platform の @features は参照しない。IADR-0057）。
import type { FeatureModule } from '@foundation/routing/featureRegistry';

export const sampleFeature: FeatureModule = {
  id: 'sample',
  routes: [
    // 例: { path: 'sample', element: <SamplePage /> } を追加する。
  ],
  nav: { label: 'Sample', to: '/sample' },
};
