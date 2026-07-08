import { features } from '@features/index';
import type { FeatureNav } from './featureRegistry';

// Issue #136 / IADR-0035: features が公開するナビ項目を登録順に集約する。routing は既に @features に
// 依存しており（router.tsx）、ナビ導出も同層に置いて foundation/ui → features の逆依存を作らない。
// 表示のロール絞り込み（存在秘匿）は呼び出し側（Layout）で行う。
export interface NavItem extends FeatureNav {
  id: string;
}

export function navItems(): NavItem[] {
  return features
    .filter((f): f is typeof f & { nav: FeatureNav } => f.nav !== undefined)
    .map((f) => ({ id: f.id, ...f.nav }));
}
