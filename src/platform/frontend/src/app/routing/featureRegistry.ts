import type { MessageDescriptor } from '@lingui/core';
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
 * **本リポジトリの計画に属さないユニット**（AST。Lingui のカタログを持たない）のためである。
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
   * 省略できるのは**本計画に属さないユニット**（AST）のためだけで、
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
 * **総称としての『その他』は使わない**」と定めた。ユニット自身（AST）は本リポジトリの計画に
 * 属さず `group` を宣言しないため、**機能名は合成点が与える**（IADR-0125 決定 9）。
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
// パンくず（05_screens §共通シェル「パンくず・権限バッジ」。#446）
// ---------------------------------------------------------------------------

/**
 * パンくずの親画面の段。
 *
 * hi-fi / wireframe モックの crumb は、親画面の段を `<a>` で描いている
 * （SC-03 の `検索結果`・SC-07 の `データソース管理`・SC-11 の `ダッシュボード`・
 *  SC-12 の `ABAC設定`）。実在する到達可能な画面なのでリンクにする。
 */
export interface BreadcrumbCrumb {
  /** 段の表示名。解決は描画時（`NavLabel` と同じ理由）。 */
  label: NavLabel;
  /** 遷移先パス（例: "/admin/sources"）。 */
  to: string;
}

/**
 * 1 画面のパンくず宣言（05_screens §共通シェル「パンくず・権限バッジ」）。
 *
 * 🔴 **パンくずは左ナビの `group` / `label` からは導けない。** モックアップを実測すると、
 * (1) 親画面の段を持つ画面がある（SC-02 / SC-03 / SC-07 / SC-11 / SC-12）、
 * (2) 葉が実行時にしか決まらない画面がある（SC-03 の文書タイトル）、
 * (3) crumb の表示名はナビの表示名と一致しない（ナビ「検索・質問」／crumb「検索・チャット質問」）。
 * よって**宣言データ**として持つ。
 *
 * 🔴 **ナビ項目とは別の登録面である。** SC-03 は左ナビに置かない（計画 §共通シェル
 * ［2026-08-05 確定］。ルートが文書 ID を必須とするため）が、パンくずは持つ。
 * ナビ項目へ相乗りさせると SC-03 を表現できない。
 */
export interface FeatureBreadcrumb {
  /**
   * 対象ルートの**完全パス**（TanStack の `fullPath`。例 `/docs/$id`）。宣言の主キー。
   * 共通シェルは「いま居るルート」しか知らないため、ここで突き合わせる。
   */
  routePath: string;
  /**
   * 画面グループ（05_screens §共通シェル の 4 グループ）。
   * **`user` はグループ段を描かない**（モックアップ実測: SC-01/02/03/04/08/18/21 は
   * `ホーム / <画面名>` の 2 段で、グループ段を持たない）。判定は `breadcrumbTrail()` が行う。
   */
  group: NavGroup;
  /** 親画面の段（上位から順）。持たない画面は省略する。 */
  parents?: readonly BreadcrumbCrumb[];
  /**
   * 自画面の段。
   * **動的な葉を持つ画面（SC-03 の文書タイトル）は宣言しない** —— 実行時に与える
   * （`useBreadcrumbLeaf`）。取得前は葉を描かない（未確定の文字列を出さない）。
   */
  label?: NavLabel;
  /**
   * 表示に必要なロール（いずれか一致）。省略時は認証済み全員。
   *
   * 🔴 **存在秘匿（IADR-0009）の経路である。** 権限外の画面でパンくずを描くと、
   * `NotFound` の外側から「そのパスは実在し、運用グループの構成ビューアである」ことが読める。
   * **ルートの `RequireRole anyOf` および左ナビの `requiresAnyRole` と同じ値を置く**
   * （一致は `breadcrumbs.test.ts` が固定する）。
   */
  requiresAnyRole?: readonly string[];
}
