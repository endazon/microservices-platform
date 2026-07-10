// テンプレート: ユニットのサンプル feature。
// 実装では @foundation の FeatureModule 型に沿ってルート・ナビゲーション・コンポーネントを定義する。
// 依存規則: 可変ユニットは @foundation のみ参照可（platform の @features は参照しない。IADR-0057）。
import type { FeatureModule } from '@foundation/routing/featureRegistry';

export const sampleFeature: FeatureModule = {
  // 例: id / path / navLabel / element などを FeatureModule の定義に合わせて記述する。
  // ここではテンプレートのため最小のプレースホルダに留める。
} as FeatureModule;
