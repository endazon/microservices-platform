import { i18n } from '@lingui/core';
import { msg } from '@lingui/core/macro';
import type { BreadcrumbCrumb, FeatureBreadcrumb, NavLabel } from './featureRegistry';
import { PLAN_NAV_GROUP_MESSAGES, resolveNavLabel } from './nav';
import { ENTRY_ROUTE_PATH } from './entryPath';

// 05_screens §共通シェル「パンくず・権限バッジ: 上部にパンくずと画面グループのバッジ
// （管理／システム管理／運用）を表示する」（#446）。
//
// IADR-0124 決定 1: 本モジュールは**合成点（@features）を import しない**。
// 共通シェル（`app/Layout`）はここに登録されたデータを読むだけで、
// 可変ユニットを参照しない。登録するのは合成点を知る唯一の場所（router.tsx）である。
//
// 🔴 **登録面をナビと分けているのは SC-03 のためである。** SC-03（文書詳細）は
// 左ナビに置かない（計画 §共通シェル ［2026-08-05 確定］）が、パンくずは持つ。
export type { BreadcrumbCrumb, FeatureBreadcrumb };

/** パンくずの「ホーム」段の文言。左ナビの見出しと同じく描画時に解決する。 */
export const BREADCRUMB_HOME_MESSAGE = msg`ホーム`;

/** パンくず全体のランドマーク名（`<nav aria-label>`）。 */
export const BREADCRUMB_NAV_LABEL_MESSAGE = msg`パンくず`;

/** 段の種別。描画側が「バッジにする段」「リンクにする段」を色分けなしで判別できるようにする。 */
export type BreadcrumbSegmentKind = 'home' | 'group' | 'parent' | 'current';

/** 描画用に解決済みの 1 段（`NavItemView` と同じ作法で、i18n の解決点を 1 か所に閉じる）。 */
export interface BreadcrumbSegmentView {
  kind: BreadcrumbSegmentKind;
  /** 解決済みの表示名。 */
  label: string;
  /**
   * 遷移先。**持たない段はリンクにしない。**
   * グループ段（バッジ）と現在地の段は常に `undefined` である。
   */
  to?: string;
}

let registeredBreadcrumbs: readonly FeatureBreadcrumb[] = [];

/** 合成点を知る唯一の場所（router.tsx）が起動時に 1 度だけ登録する。 */
export function registerBreadcrumbs(declarations: readonly FeatureBreadcrumb[]): void {
  registeredBreadcrumbs = declarations;
}

/** 登録済みのパンくず宣言（登録順）。 */
export function breadcrumbs(): readonly FeatureBreadcrumb[] {
  return registeredBreadcrumbs;
}

/** 宣言のうち、指定したロールの利用者に見せてよいものを引く。 */
function isVisible(declaration: FeatureBreadcrumb, roles: readonly string[]): boolean {
  const required = declaration.requiresAnyRole;
  if (!required || required.length === 0) return true;
  return required.some((r) => roles.includes(r));
}

function segment(kind: BreadcrumbSegmentKind, label: NavLabel, to?: string): BreadcrumbSegmentView {
  return to === undefined
    ? { kind, label: resolveNavLabel(label) }
    : { kind, label: resolveNavLabel(label), to };
}

export interface BreadcrumbTrailOptions {
  /** いま居るルートの完全パス（TanStack の `fullPath`）。未確定なら `undefined`。 */
  routePath: string | undefined;
  /** 実行時に画面が与える葉（SC-03 の文書タイトル）。取得前は `undefined`。 */
  leaf?: string;
  /** 現在の利用者のロール。 */
  roles?: readonly string[];
  /** 宣言（既定は登録済みのもの。テストは直接渡す）。 */
  declarations?: readonly FeatureBreadcrumb[];
}

/**
 * パンくずの段を組み立てる（05_screens §共通シェル）。
 *
 * **空配列は「パンくずを描かない」を意味する。** 返し方を 1 つにしているのは、
 * 「宣言が無い」「権限が無い」「共通シェル適用外」が描画側で区別できてしまうと、
 * そこから資源の存在を推測できるためである（IADR-0009）。
 *
 * 段の並びは `ホーム / [グループ] / [親画面…] / [現在地]`。
 * - **`ホーム`** は既定ルート（`ENTRY_ROUTE_PATH`）へのリンク（IADR-0124 決定 6）。
 * - **グループ段は `user` のとき出さない**（モックアップ実測）。**リンクにもしない**
 *   （モックの crumb でグループ段は `<a>` ではない）。描画側はここをバッジにする。
 * - **現在地の段はリンクにしない**（`to` を持たない）。
 * - **自画面の段は `label` も `leaf` も無ければ出さない**（未確定の文字列を描かない）。
 * - **葉が無く、末尾の親の段が自ルートを指すなら、その段が現在地である**（リンクにしない。
 *   SC-04 の `ホーム / Wiki`。#1200 —— 葉を持つ画面が自画面の名を親の段に置くと、葉の無いときに
 *   「いま居る画面へのリンク」が現在地の位置に立つ。段の構成は宣言のまま、リンクだけを外す）。
 */
export function breadcrumbTrail({
  routePath,
  leaf,
  roles = [],
  declarations = breadcrumbs(),
}: BreadcrumbTrailOptions): BreadcrumbSegmentView[] {
  if (routePath === undefined) return [];
  const declaration = declarations.find((d) => d.routePath === routePath);
  if (!declaration) return [];
  // 存在秘匿: 権限外では 1 段も描かない（未知パスと同じ描画になる）。
  if (!isVisible(declaration, roles)) return [];

  const trail: BreadcrumbSegmentView[] = [
    segment('home', BREADCRUMB_HOME_MESSAGE, ENTRY_ROUTE_PATH),
  ];
  // 05_screens §共通シェル: グループの語は左ナビの見出しと同じものを使う
  // （2 か所で別々に訳すと「管理」と「管理者」のように食い違う）。
  if (declaration.group !== 'user') {
    trail.push(segment('group', PLAN_NAV_GROUP_MESSAGES[declaration.group]));
  }
  for (const parent of declaration.parents ?? []) {
    trail.push(segment('parent', parent.label, parent.to));
  }
  const current = declaration.label ?? leaf;
  if (current !== undefined) {
    trail.push(segment('current', current));
    return trail;
  }
  // 葉が無い（取得前・ページを開いていない）。末尾の親の段が自ルートを指すなら現在地に格下げする。
  const last = trail.at(-1);
  if (last?.kind === 'parent' && last.to === routePath) {
    trail[trail.length - 1] = { kind: 'current', label: last.label };
  }
  return trail;
}

/** `<nav aria-label>` に使う解決済みの文言。 */
export function breadcrumbNavLabel(): string {
  return i18n._(BREADCRUMB_NAV_LABEL_MESSAGE);
}
