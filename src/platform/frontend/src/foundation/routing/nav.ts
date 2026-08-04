import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
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

/**
 * グループの見出し（05_screens §共通シェル）。
 *
 * ADR-0031 / IADR-0125 決定 6: 文言は Lingui のマクロで抽出する。ここはモジュール初期化時に
 * 評価されるため、翻訳済みの**文字列**ではなく `MessageDescriptor` を持ち、描画時に
 * `i18n._()` で解決する（モジュール読み込み時に文字列へ確定させると、ロケール切替に追随しない）。
 */
export const NAV_GROUP_MESSAGES: Record<(typeof NAV_GROUP_ORDER)[number], MessageDescriptor> = {
  user: msg`利用者`,
  personal: msg`個人`,
  admin: msg`管理`,
  ops: msg`運用`,
  other: msg`その他`,
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
    label: i18n._(NAV_GROUP_MESSAGES[id]),
    items: items.filter((i) => (i.group ?? OTHER) === id),
  })).filter((g) => g.items.length > 0);
}
