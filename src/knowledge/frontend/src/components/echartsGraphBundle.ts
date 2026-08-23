// SC-18, ADR-0039, IADR-0274 (#917): グラフ描画（GraphChart）の登録層。
//
// 🔴 **`echartsBundle.ts`（折れ線・棒）とは別モジュールに分けてある。**
// 理由は初期ロードではなく**画面間の分離**である —— SC-08 / SC-10（図）と SC-18（グラフ）は
// 別ルートの遅延チャンクであり、登録層を 1 本に束ねると片方しか開かない利用者にも両方の面が
// 届く。バレルを動的 import すると tree-shaking が効かない（実測 1,092.40 kB）ため、
// ここでも**静的な名前つき import** を使い、このモジュールごと動的に import する
// （`echartsGraphLoader.ts`。`echartsBundle.ts` の冒頭の注記と同じ理屈）。
//
// SVG レンダラを使う理由・外部 egress を持ち込まない規約は `echartsLoader.ts` の冒頭を参照
// （jsdom に canvas が無い／08_data-egress-policy。実測でも SVG は 200 ノード / 500 辺の
// 対話操作に足りる —— IADR-0274 §条件 1）。
import * as echarts from 'echarts/core';
import { GraphChart } from 'echarts/charts';
import { TooltipComponent } from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';

echarts.use([GraphChart, TooltipComponent, SVGRenderer]);

export { echarts };
