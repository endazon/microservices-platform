import type { MessageDescriptor } from '@lingui/core';
import type { ReactNode } from 'react';
import { createRoute } from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import type { ShellRoute } from './shell';

// ADR-0031 / IADR-0124: 可変機能ユニット（knowledge / AST ほか）が platform の共通シェルへ
// 画面を差し込むための契約。ユニットは platform を import せず、shell を引数で受け取る。

/**
 * 左ナビのグループ（05_screens §共通シェル の **4 グループ**）。
 *
 * 本計画に属さない可変機能ユニット（AST 等）の画面はここへ入れない。合成点が
 * **ユニットの機能名**を見出しにしたグループ（`UnitNavGroup`）へ束ねる
 * （05_screens §共通シェル ［2026-08-04 確定］。**総称としての「その他」は使わない**）。
 */
export type NavGroup = 'user' | 'personal' | 'admin' | 'ops';

/**
 * ナビ表示名。
 *
 * ADR-0031 / IADR-0125 決定 6（#502 で拡張）: 文言は Lingui のカタログへ載せる。ナビ項目は
 * **モジュール初期化時に評価される**ため、翻訳済みの文字列で持つとロケール切替に追随しない
 * （`nav.ts` の `PLAN_NAV_GROUP_MESSAGES` が既に同じ理由で `MessageDescriptor` を持っている）。
 * よって `MessageDescriptor` を受け付け、解決は描画時（`navGroups()`）に行う。
 *
 * `string` も残すのは、まだ i18n 化していない画面（SC-04〜11。#452 の残り分割が引き受ける）と、
 * 本リポジトリから変更できない旧契約ユニット（AST。IADR-0120）のためである。
 */
export type NavLabel = string | MessageDescriptor;

// Issue #136 / IADR-0035: 共通ナビへ出すメニュー項目。権限外には表示しない（存在秘匿の UI 表現）。
export interface FeatureNav {
  /** ナビ表示名（例: "運用ダッシュボード"）。 */
  label: NavLabel;
  /** 遷移先パス（例: "/admin/ops"）。 */
  to: string;
  /** 表示に必要なロール（いずれか一致で表示）。省略時は認証済み全員に表示する。 */
  requiresAnyRole?: string[];
  /**
   * 左ナビのグループ（05_screens §共通シェル の 4 グループ）。
   *
   * **本計画に属するユニットは必ず宣言する**（型で強制する。`PlanNavItem`）。
   * 省略できるのは本リポジトリから変更できない旧契約ユニット（AST。IADR-0120）のためだけで、
   * その項目は合成点が **ユニットの機能名**のグループへ束ねる（`UnitNavGroup`）。
   * **総称としてのフォールバック（「その他」）は持たない**（05_screens §共通シェル ［2026-08-04 確定］）。
   */
  group?: NavGroup;
}

/** ナビ項目に由来 feature の識別子を添えたもの（描画の key と診断に使う）。 */
export interface NavItem extends FeatureNav {
  id: string;
}

/**
 * 計画の 4 グループのいずれかを**必ず宣言した**ナビ項目。
 *
 * 本計画に属するユニット（`@knowledge` ほか）が公開する項目はこの型で受ける。
 * `group` を省略できる `NavItem` のまま受けると、宣言し忘れた項目が
 * **どのグループにも属さず静かに消える**（総称フォールバックを廃止したため）。型で塞ぐ。
 */
export type PlanNavItem = NavItem & { group: NavGroup };

/**
 * 本計画に属さない可変機能ユニットのナビグループ（05_screens §共通シェル ［2026-08-04 確定］）。
 *
 * 計画は「実装側でグループを設けて分類してよい。**ただしグループ名は『ユニットの機能名』とする**
 * （例: `ai-stock-trading` → 「株式自動売買」）。並び順は計画の 4 グループの後とする。
 * **総称としての『その他』は使わない**」と定めた。ユニット自身（AST）は本リポジトリから
 * 変更できない（IADR-0120）ため、**機能名は合成点が与える**（IADR-0125 決定 9）。
 */
export interface UnitNavGroup {
  /** グループ ID（ユニット名。描画の key と診断に使う）。 */
  id: string;
  /** 見出し＝**ユニットの機能名**。ロケール切替に追随させるため描画時に解決する。 */
  label: MessageDescriptor;
  /** このユニットの画面のナビ項目。 */
  items: readonly NavItem[];
}

/**
 * ユニットが公開するルート factory（IADR-0124 決定 1）。
 *
 * **実装側でこの型を注釈として使ってはならない。** 戻り値へ `readonly AnyRoute[]` が付くと
 * ルート ID とパスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
 * （IADR-0124 §実測）。本型は契約の形を説明するための参考型である。
 */
export type UnitRouteFactory = (shell: ShellRoute) => readonly AnyRoute[];

// ---------------------------------------------------------------------------
// 旧契約（互換ブリッジ。IADR-0124 決定 2）
// ---------------------------------------------------------------------------

/**
 * 旧契約のルート宣言。
 *
 * かつて `react-router-dom` の `RouteObject` だったものを、同じ形のまま自前の型へ移した。
 * `react-router-dom` を依存から外しつつ、本リポジトリから変更できないユニットの
 * object literal をそのまま受け付けるためである。
 *
 * @deprecated 新規のユニット・画面は型付きルート factory で公開する（`UnitRouteFactory`）。
 */
export interface LegacyFeatureRoute {
  /** 共通シェル配下のパス（旧契約は相対表記。例: "settings"）。 */
  path: string;
  /** 描画する要素。 */
  element: ReactNode;
}

/**
 * 旧契約の feature モジュール。
 *
 * 本リポジトリから変更できない可変ユニット（`src/ai-stock-trading`。IADR-0120）が
 * この形で features を公開しているため、契約の**形を変えずに**残している。
 * ここで宣言されたルートは型付きルート木の外側にあり、実行時にだけ共通シェルへ接ぎ木される
 * （型付きの配列へ混ぜると全ユニットの型安全が失われる。IADR-0124 §実測）。
 *
 * @deprecated 新規のユニットは型付きルート factory ＋ `NavItem` で公開する。
 */
export interface FeatureModule {
  /** 一意な識別子（例: "sc01-settings"）。 */
  id: string;
  /** 共通シェル配下に載る子ルート群。 */
  routes: LegacyFeatureRoute[];
  /** 共通ナビへ出す項目（任意）。権限外には表示しない（Issue #136 / IADR-0035）。 */
  nav?: FeatureNav;
}

/**
 * 旧契約の feature 群を TanStack のルートへ変換する（IADR-0124 決定 2）。
 * 戻り値は `AnyRoute[]`＝型情報を持たない。呼び出し側は**型付きの木を組んだ後**に足すこと。
 */
export function createLegacyRoutes(
  shell: ShellRoute,
  modules: readonly FeatureModule[],
): AnyRoute[] {
  return modules.flatMap((m) =>
    m.routes.map((r) =>
      createRoute({
        getParentRoute: () => shell,
        // 旧契約のパスは共通シェル配下の相対表記（"settings"）。TanStack は絶対表記を取る。
        path: r.path.startsWith('/') ? r.path : `/${r.path}`,
        component: () => r.element,
      }),
    ),
  );
}

/** 旧契約の feature 群からナビ項目を取り出す。 */
export function legacyNavItems(modules: readonly FeatureModule[]): NavItem[] {
  return modules
    .filter((m): m is FeatureModule & { nav: FeatureNav } => m.nav !== undefined)
    .map((m) => ({ id: m.id, ...m.nav }));
}
