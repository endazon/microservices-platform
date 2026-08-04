import type { ReactNode } from 'react';
import { createRoute } from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import type { ShellRoute } from './shell';

// ADR-0031 / IADR-0124: 可変機能ユニット（knowledge / AST ほか）が platform の共通シェルへ
// 画面を差し込むための契約。ユニットは platform を import せず、shell を引数で受け取る。

/** 左ナビのグループ（05_screens §共通シェル の 4 グループ。未宣言は「その他」へ落ちる）。 */
export type NavGroup = 'user' | 'personal' | 'admin' | 'ops';

// Issue #136 / IADR-0035: 共通ナビへ出すメニュー項目。権限外には表示しない（存在秘匿の UI 表現）。
export interface FeatureNav {
  /** ナビ表示名（例: "運用ダッシュボード"）。 */
  label: string;
  /** 遷移先パス（例: "/admin/ops"）。 */
  to: string;
  /** 表示に必要なロール（いずれか一致で表示）。省略時は認証済み全員に表示する。 */
  requiresAnyRole?: string[];
  /**
   * 左ナビのグループ（05_screens §共通シェル）。省略した項目は「その他」へ置く
   * （本リポジトリの計画に属さないユニット＝AST 等が該当する）。
   */
  group?: NavGroup;
}

/** ナビ項目に由来 feature の識別子を添えたもの（描画の key と診断に使う）。 */
export interface NavItem extends FeatureNav {
  id: string;
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
export function createLegacyRoutes(shell: ShellRoute, modules: readonly FeatureModule[]): AnyRoute[] {
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
