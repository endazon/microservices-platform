import { msg } from '@lingui/core/macro';
import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { ShellRoute } from '@foundation/routing/shell';
import type { FeatureBreadcrumb, PlanNavItem } from '@foundation/routing/featureRegistry';

// SC-18, UC-10, FR-17/FR-05: ナレッジグラフビュー（05_screens: ルート /graph。
// 起点・探索深さはクエリで持つ。例: /graph?root=<uuid>&hops=2）。読み取り専用。
// **ロール限定は無い**（05_screens §共通シェル: 利用者グループは ABAC の権限内で全利用者が
// 利用できる）。可視性はサーバ側（/bff/graph の deny-by-default・存在秘匿）が決める。
// ADR-0031 / IADR-0124 決定 1: ルートは型付き factory で公開する（戻り値へ型注釈を付けない）。

// NFR, ADR-0031 / IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない。
// ECharts の graph 面はさらにその先の動的 import —— echartsGraphLoader.ts）。
const GraphViewPage = lazyRouteComponent(
  () => import('../components/GraphViewPage'),
  'GraphViewPage',
);

/** 探索深さの選択肢（05_screens §SC-18: 1 / 2 / 3 のみ。既定 2・上限 3）。 */
export const HOPS_OPTIONS = [1, 2, 3] as const;
type HopsOption = (typeof HOPS_OPTIONS)[number];

/** 探索深さが選択肢（1 / 2 / 3）のどれかか。範囲外は**丸めずエラー**にする（05_screens §SC-18 入力表）。 */
export function isHopsOption(hops: number): hops is HopsOption {
  return (HOPS_OPTIONS as readonly number[]).includes(hops);
}

/** 間引きの基準（ADR-0049 決定 4 の 3 択）。 */
export const THINNING_OPTIONS = ['distance', 'updated', 'degree'] as const;
export type ThinningOption = (typeof THINNING_OPTIONS)[number];

export interface GraphSearch {
  /** 起点文書 ID。空文字は「未指定」（起点の指定を促す案内を出す）。 */
  root: string;
  /**
   * 探索深さ。URL の値を**そのまま**持つ（#2021）。選択肢の外（例 4）でも既定の 2 へ丸めない ——
   * 画面がエラーを示し、近傍を引かない（`isHopsOption` で判定する）。指定が無いときだけ既定 2。
   */
  hops: number;
  by: ThinningOption;
  /** 辺の型フィルタ（型 ID の配列）。省略＝すべての型。 */
  types?: string[];
}

export const createSc18GraphRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/graph',
    // SC-18, IADR-0124: URL が起点・深さ・間引き・型フィルタの単一情報源である。
    // URL は外部由来である。［2026-10-24 / #2021］hops は**丸めない**: 05_screens §SC-18 の入力表は
    // 「1 / 2 / 3 のみ。上限超過は丸めずエラー」と定める。従前は不正値を既定 2 へ倒していたが、
    // 利用者は 4 を求めて 2 の結果を見ることになり、計画に反していた。範囲外は値のまま持ち、
    // 画面がエラーを示して近傍を引かない（サーバの 400 も引き続き防壁である）。
    validateSearch: (raw: Record<string, unknown>): GraphSearch => {
      const hops = raw.hops === undefined || raw.hops === '' ? 2 : Number(raw.hops);
      const by = THINNING_OPTIONS.find((b) => b === raw.by) ?? 'distance';
      const types = Array.isArray(raw.types)
        ? raw.types.filter((t): t is string => typeof t === 'string')
        : typeof raw.types === 'string'
          ? [raw.types]
          : undefined;
      return {
        root: typeof raw.root === 'string' ? raw.root : '',
        hops,
        by,
        ...(types && types.length > 0 ? { types } : {}),
      };
    },
    component: GraphViewPage,
  });

// 05_screens §共通シェル: 左ナビ「利用者」グループの「ナレッジグラフ」（hi-fi モックの左レール準拠）。
export const sc18GraphNav: PlanNavItem = {
  id: 'sc18-graph',
  label: msg`ナレッジグラフ`,
  to: '/graph',
  group: 'user',
};

// 05_screens §共通シェル / #446: パンくず `ホーム / ナレッジグラフ`。
// 🔴 **モックの crumb は「知識グラフ」だが、計画の画面名・左ナビは「ナレッジグラフ(ビュー)」**である。
// 計画は「同じものに 2 つの名前があると食い違う」ことを名指しで避けている（§用語）ので、
// シェルの中で 1 つの名前に揃える。表記ゆれは計画へ環流する。
export const sc18GraphBreadcrumb: FeatureBreadcrumb = {
  routePath: '/graph',
  group: 'user',
  label: msg`ナレッジグラフ`,
};
