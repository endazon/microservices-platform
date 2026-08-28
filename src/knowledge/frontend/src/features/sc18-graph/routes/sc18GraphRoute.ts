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

/** 間引きの基準（ADR-0049 決定 4 の 3 択）。 */
export const THINNING_OPTIONS = ['distance', 'updated', 'degree'] as const;
export type ThinningOption = (typeof THINNING_OPTIONS)[number];

export interface GraphSearch {
  /** 起点文書 ID。空文字は「未指定」（起点の指定を促す案内を出す）。 */
  root: string;
  hops: HopsOption;
  by: ThinningOption;
  /** 辺の型フィルタ（型 ID の配列）。省略＝すべての型。 */
  types?: string[];
}

export const createSc18GraphRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/graph',
    // SC-18, IADR-0124: URL が起点・深さ・間引き・型フィルタの単一情報源である。
    // URL は外部由来なので正規化する —— hops の不正値は**クライアントでは既定 2 へ**倒す
    // （選択肢が 1/2/3 しか無い UI に「エラー状態」を持ち込まない。手打ちの hops=4 を
    // そのまま送ってサーバの 400 を見せる形にはしない。丸めずエラーの防壁はサーバに在る）。
    validateSearch: (raw: Record<string, unknown>): GraphSearch => {
      const hops = HOPS_OPTIONS.find((h) => h === Number(raw.hops)) ?? 2;
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
