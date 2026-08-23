// ADR-0031 §採用技術一覧「チャート = Apache ECharts（自己ホスト）」/ 08_data-egress-policy /
// NFR・IADR-0134（初期ロード予算）/ IADR-0121 決定 1 の第 4 段（#788）。
//
// ■ 本ファイルが扱うのは**折れ線・棒の面（SC-08 / SC-10）だけ**である
//   グラフ描画（GraphChart・SC-18）は別の登録層・読み込み口（echartsGraphBundle.ts /
//   echartsGraphLoader.ts）が同じ作法で持つ（#917 / IADR-0274）。束ねない理由はあちらの冒頭を参照。
//
// ■ なぜ読み込みを 1 箇所に閉じるか
//   ECharts は本リポジトリで最大級の依存である。**静的 import すると初期ロードへ載る**
//   （IADR-0134 の ratchet に直撃する）。読み込みをこの関数に閉じ、呼び出し側は
//   `useEffect` の中から `await` する——動的 import なので独立した遅延チャンクになる。
//
// ■ なぜ `echarts` ではなく `echarts/core` か
//   全部入り（`import * as echarts from 'echarts'`）は使わない機能まで束ねる。
//   使う 2 種（折れ線・棒）と、軸・ツールチップ・凡例だけを登録する。
//
// ■ なぜ登録を `echartsBundle.ts` へ分けるか（🔴 これを間違えると tree-shaking が効かない）
//   `echarts/charts` 等は**バレル**であり、`await import('echarts/charts')` と書くと
//   bundler は名前空間全体を要求されたと解釈して**何も落とさない**（実測 1,092.40 kB）。
//   静的な名前つき import なら落とせるので、登録だけを別モジュールへ閉じ、
//   **そのモジュールごと**動的に import する。遅延は保たれる。
//
// ■ なぜ SVG レンダラか
//   (1) jsdom には canvas が無く、Canvas レンダラだと単体テストで描画が落ちる。
//   (2) 成果物に canvas 依存を持ち込まない。
//
// ■ 外部 egress を持ち込まない（08_data-egress-policy）
//   ECharts は npm から自己ホストする。CDN・Web フォント・テレメトリは使わない。
//   図のフォントは指定せず、`@platform/ui` のシステムフォントを継承させる。
//   この禁止は `node scripts/check-static-egress.js --require` が成果物を走査して強制する。

/** 遅延読み込みした echarts のうち、本リポジトリが使う面だけを表す型。 */
export interface EChartsModule {
  init: (
    el: HTMLElement,
    theme?: string | null,
    opts?: { renderer?: 'svg' | 'canvas' },
  ) => EChartsInstance;
}

export interface EChartsInstance {
  setOption: (option: unknown) => void;
  resize: () => void;
  dispose: () => void;
}

/** 一度だけ解決する。画面を行き来するたびに import を走らせない。 */
let cached: Promise<EChartsModule> | null = null;

export function loadECharts(): Promise<EChartsModule> {
  cached ??= (async () => {
    const { echarts } = await import('./echartsBundle');
    return echarts as unknown as EChartsModule;
  })();
  return cached;
}

/** テスト専用: モジュールキャッシュを捨てる。 */
export function resetEChartsCacheForTest(): void {
  cached = null;
}
