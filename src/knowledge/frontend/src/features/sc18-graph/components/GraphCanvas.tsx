import { useEffect, useRef } from 'react';
import { loadGraphECharts } from '../../../components/echartsGraphLoader';
import type { GraphEChartsInstance } from '../../../components/echartsGraphLoader';

// SC-18, UC-10, FR-17, ADR-0039 (#917): グラフ描画の器。
//
// ■ 読み込みは遅延（echartsGraphLoader の動的 import）。初期ロードへ載せない（IADR-0134）。
// ■ アクセシビリティ: 器に `role="img"` と `aria-label`（翻訳済み文字列）を付ける。
//   グラフの意味（種別・線種の凡例）は GraphLegend のテキストが持ち、**色だけに頼らない**。
// ■ 描けなくても投げない —— 空状態・サイドパネル・帯は器の外にあり、そこまで巻き込まない
//   （EChart.tsx と同じ判断）。

export interface GraphCanvasProps {
  /** `buildGraphOption` が組んだ option。 */
  option: Record<string, unknown>;
  /** 図の要約（読み上げ用）。**翻訳済みの文字列**を渡す。 */
  ariaLabel: string;
  /** ノード（文書）をクリックしたとき。id は文書 ID。 */
  onNodeClick?: (documentId: string) => void;
  className?: string;
}

export function GraphCanvas({ option, ariaLabel, onNodeClick, className }: GraphCanvasProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<GraphEChartsInstance | null>(null);
  // クリック購読は初期化時に 1 回だけ張る。最新のハンドラは ref 経由で届ける
  // （option 更新のたびに on/off を張り替えない）。
  const onNodeClickRef = useRef(onNodeClick);
  onNodeClickRef.current = onNodeClick;

  useEffect(() => {
    let disposed = false;
    const el = containerRef.current;
    if (!el) return;

    void (async () => {
      try {
        const echarts = await loadGraphECharts();
        if (disposed) return;
        if (!chartRef.current) {
          const chart = echarts.init(el, null, { renderer: 'svg' });
          chart.on('click', (params) => {
            if (params.dataType === 'node' && typeof params.data?.id === 'string') {
              onNodeClickRef.current?.(params.data.id);
            }
          });
          chartRef.current = chart;
        }
        chartRef.current.setOption(option);
      } catch (err) {
        // 投げない（冒頭の注記）。グラフ以外の要素（帯・凡例・パネル）は生きたまま残す。
        console.error('graph rendering failed:', err);
      }
    })();

    return () => {
      disposed = true;
      chartRef.current?.dispose();
      chartRef.current = null;
    };
  }, [option]);

  return (
    <div
      ref={containerRef}
      role="img"
      aria-label={ariaLabel}
      className={className ?? 'h-[32rem] w-full'}
      data-testid="graph-canvas"
    />
  );
}
