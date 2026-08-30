import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';
import type { NavGroup, NavItem, NavLabel, PlanNavItem, UnitNavGroup } from './featureRegistry';

// Issue #136 / IADR-0035: 共通シェルの左ナビ（05_screens §共通シェル）。
// IADR-0124 決定 1: 本モジュールは**合成点（@features）を import しない**。
// foundation/ui（Layout）はナビを読むため、ここが features を直接参照すると
// 「foundation → 可変ユニット」の逆依存が共通シェルの経路に生まれる。
// 代わりに router.tsx（合成点を知る唯一の場所）が起動時に登録する。
export type { NavItem, NavGroup, NavLabel, PlanNavItem, UnitNavGroup };

/**
 * 計画の左ナビのグループ ID と表示順（05_screens §共通シェル の **4 グループ**）。
 *
 * 本計画に属さないユニットのグループはここに含めない——それらは**ユニットの機能名**を
 * 見出しとする別のグループとして、**この 4 つの後ろ**に並ぶ
 * （05_screens §共通シェル ［2026-08-04 確定］）。
 */
export const PLAN_NAV_GROUP_ORDER = ['user', 'personal', 'admin', 'ops'] as const;

/**
 * 計画の 4 グループの見出し（05_screens §共通シェル）。
 *
 * ADR-0031 / IADR-0125 決定 6: 文言は Lingui のマクロで抽出する。ここはモジュール初期化時に
 * 評価されるため、翻訳済みの**文字列**ではなく `MessageDescriptor` を持ち、描画時に
 * `i18n._()` で解決する（モジュール読み込み時に文字列へ確定させると、ロケール切替に追随しない）。
 */
export const PLAN_NAV_GROUP_MESSAGES: Record<NavGroup, MessageDescriptor> = {
  user: msg`利用者`,
  personal: msg`個人`,
  admin: msg`管理`,
  ops: msg`運用`,
};

/**
 * 描画用に解決済みのナビ項目（#502）。
 *
 * `NavItem.label` は `string | MessageDescriptor` だが、共通シェル（`app/Layout`）へは
 * **解決済みの文字列**だけを渡す。分岐を描画側へ持ち出すと、i18n の解決点が 2 か所になり、
 * 「片方だけ翻訳される」形の欠陥を作れてしまう。
 */
export interface NavItemView extends Omit<NavItem, 'label'> {
  label: string;
}

export interface NavGroupView {
  /** 計画の 4 グループは固定 ID、ユニットのグループはユニット名。 */
  id: string;
  label: string;
  items: NavItemView[];
}

/** ナビ表示名を描画時に解決する（`MessageDescriptor` は現在のロケールで翻訳する）。 */
export function resolveNavLabel(label: NavLabel): string {
  return typeof label === 'string' ? label : i18n._(label);
}

let registeredPlanItems: readonly PlanNavItem[] = [];
let registeredUnitGroups: readonly UnitNavGroup[] = [];

/**
 * 合成点を知る唯一の場所（router.tsx）が起動時に 1 度だけ登録する。
 * 受けるのは**計画の 4 グループを宣言した項目**だけである（`PlanNavItem`）。
 */
export function registerNavItems(items: readonly PlanNavItem[]): void {
  registeredPlanItems = items;
}

/**
 * 本計画に属さない可変機能ユニットのグループを登録する
 * （05_screens §共通シェル ［2026-08-04 確定］。IADR-0125 決定 9）。
 */
export function registerUnitNavGroups(groups: readonly UnitNavGroup[]): void {
  registeredUnitGroups = groups;
}

/** 登録済みの計画グループ向けナビ項目（登録順）。 */
export function navItems(): readonly PlanNavItem[] {
  return registeredPlanItems;
}

/** 登録済みのユニットグループ（登録順）。 */
export function unitNavGroups(): readonly UnitNavGroup[] {
  return registeredUnitGroups;
}

/**
 * ナビ項目をグループへ束ねる（05_screens §共通シェル）。
 *
 * 並びは **計画の 4 グループ → ユニットのグループ（機能名）** の順である。
 * 空のグループは返さない——見出しだけが残ると「権限が無くて隠れている」のか
 * 「まだ無い」のかが読めないためである。
 *
 * **総称のグループ（「その他」）は作らない**（05_screens §共通シェル ［2026-08-04 確定］）。
 * 左ナビのグループ名は利用者が機能を探す唯一の手掛かりであり、
 * 何が入っているか分からない名前を置くと導線が失われるためである。
 */
export function navGroups(
  items: readonly PlanNavItem[] = navItems(),
  groups: readonly UnitNavGroup[] = unitNavGroups(),
): NavGroupView[] {
  const resolve = (list: readonly NavItem[]): NavItemView[] =>
    list.map((i) => ({ ...i, label: resolveNavLabel(i.label) }));
  const planGroups: NavGroupView[] = PLAN_NAV_GROUP_ORDER.map((id) => ({
    id,
    label: i18n._(PLAN_NAV_GROUP_MESSAGES[id]),
    items: resolve(items.filter((i) => i.group === id)),
  }));
  const unitGroups: NavGroupView[] = groups.map((g) => ({
    id: g.id,
    label: i18n._(g.label),
    items: resolve(g.items),
  }));
  return [...planGroups, ...unitGroups].filter((g) => g.items.length > 0);
}
