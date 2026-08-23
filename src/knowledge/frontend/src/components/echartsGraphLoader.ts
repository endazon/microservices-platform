// SC-18, ADR-0039, IADR-0274 (#917): グラフ描画（GraphChart）面の遅延読み込み。
//
// `echartsLoader.ts`（折れ線・棒の面）と同じ作法である —— 読み込みをこの関数 1 本に閉じ、
// 呼び出し側は `useEffect` の中から `await` する（動的 import なので独立した遅延チャンクになる。
// 初期ロードへ載せない。IADR-0134）。登録層（`echartsGraphBundle.ts`）を別モジュールに
// 分けている理由はそちらの冒頭を参照。
//
// `EChartsInstance` を再利用せず自前の型を持つのは、グラフ面がイベント購読
// （ノード選択のための `on('click')`）と `dispatchAction`（検索フォーカス）まで使うためである。

/** ECharts のイベント引数のうち、本画面が読む面だけ。 */
interface GraphChartClickParams {
  dataType?: string;
  /** ノードの場合は `data.id`（文書 ID）。 */
  data?: { id?: string };
}

export interface GraphEChartsInstance {
  setOption: (option: unknown) => void;
  resize: () => void;
  dispose: () => void;
  on: (event: string, handler: (params: GraphChartClickParams) => void) => void;
  dispatchAction: (action: Record<string, unknown>) => void;
}

export interface GraphEChartsModule {
  init: (
    el: HTMLElement,
    theme?: string | null,
    opts?: { renderer?: 'svg' | 'canvas' },
  ) => GraphEChartsInstance;
}

/** 一度だけ解決する。画面を行き来するたびに import を走らせない。 */
let cached: Promise<GraphEChartsModule> | null = null;

export function loadGraphECharts(): Promise<GraphEChartsModule> {
  cached ??= (async () => {
    const { echarts } = await import('./echartsGraphBundle');
    return echarts as unknown as GraphEChartsModule;
  })();
  return cached;
}

/** テスト専用: モジュールキャッシュを捨てる。 */
export function resetGraphEChartsCacheForTest(): void {
  cached = null;
}
