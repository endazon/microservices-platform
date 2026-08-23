// ADR-0031 §採用技術一覧「チャート = Apache ECharts（自己ホスト）」/ #788。
//
// 🔴 **このファイルの存在理由は tree-shaking である。**
// `echarts/charts` / `echarts/components` / `echarts/renderers` は**バレル**であり、
// すべてのチャート・部品・レンダラを再輸出する。これを `await import('echarts/charts')` の形で
// 動的に読むと、bundler は名前空間オブジェクト全体を要求されたと解釈し、**何も落とせない**
// （実測: そう書いた最初の版は vendor-echarts が 1,092.40 kB になり、500 kB の上限を超えた）。
//
// **静的な名前つき import なら落とせる。** そこで「使う面だけを静的に import して登録する」層を
// このモジュールに閉じ、呼び出し側は**このモジュールごと**動的に import する
// （`echartsLoader.ts`）。遅延は保たれたまま、束ねられるのは実際に使う分だけになる。
// なお本ファイルは**折れ線・棒の面だけ**を登録する。グラフ描画（GraphChart・SC-18）の登録層は
// echartsGraphBundle.ts が別チャンクとして持つ（#917 / IADR-0274。画面間の分離のため束ねない）。
import * as echarts from 'echarts/core';
import { BarChart, LineChart } from 'echarts/charts';
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';

echarts.use([LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, SVGRenderer]);

export { echarts };
