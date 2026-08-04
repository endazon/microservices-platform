import type { NavGroup, NavItem } from './featureRegistry';

// Issue #136 / IADR-0035: 共通シェルの左ナビ（05_screens §共通シェル の 4 グループ）。
// IADR-0124 決定 1: 本モジュールは**合成点（@features）を import しない**。
// foundation/ui（Layout）はナビを読むため、ここが features を直接参照すると
// 「foundation → 可変ユニット」の逆依存が共通シェルの経路に生まれる。
// 代わりに router.tsx（合成点を知る唯一の場所）が起動時に登録する。
export type { NavItem, NavGroup };

/** グループ未宣言の項目を集める先（AST 等、本リポジトリの計画に属さないユニット）。 */
const OTHER = 'other' as const;

/** 左ナビのグループ ID と表示順（05_screens §共通シェル の 4 グループ ＋ その他）。 */
export const NAV_GROUP_ORDER = ['user', 'personal', 'admin', 'ops', OTHER] as const;

/** グループの見出し（05_screens §共通シェル）。 */
export const NAV_GROUP_LABELS: Record<(typeof NAV_GROUP_ORDER)[number], string> = {
  user: '利用者',
  personal: '個人',
  admin: '管理',
  ops: '運用',
  other: 'その他',
};

export interface NavGroupView {
  id: (typeof NAV_GROUP_ORDER)[number];
  label: string;
  items: NavItem[];
}

let registered: readonly NavItem[] = [];

/** 合成点を知る唯一の場所（router.tsx）が起動時に 1 度だけ登録する。 */
export function registerNavItems(items: readonly NavItem[]): void {
  registered = items;
}

/** 登録済みの全ナビ項目（登録順）。 */
export function navItems(): readonly NavItem[] {
  return registered;
}

/**
 * ナビ項目をグループへ束ねる（05_screens §共通シェル）。
 * 空のグループは返さない——見出しだけが残ると「権限が無くて隠れている」のか
 * 「まだ無い」のかが読めないためである。
 */
export function navGroups(items: readonly NavItem[] = navItems()): NavGroupView[] {
  return NAV_GROUP_ORDER.map((id) => ({
    id,
    label: NAV_GROUP_LABELS[id],
    items: items.filter((i) => (i.group ?? OTHER) === id),
  })).filter((g) => g.items.length > 0);
}
