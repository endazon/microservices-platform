import { useEffect, useRef } from 'react';
import { loadECharts } from './echartsLoader';
import type { EChartsInstance } from './echartsLoader';

// ADR-0031 §採用技術一覧（チャート = Apache ECharts）/ IADR-0121 決定 1 の第 4 段（#788）。
//
// ■ 図は表の**代替ではなく補助**である
//   呼び出し側は同じデータの表を必ず残す。読み込み前・読み込み失敗時にこの器は空のままだが、
//   **表があるので情報は失われない**。図が出ないことでデータが読めなくなる作りにしない。
//
// ■ アクセシビリティ
//   器に `role="img"` と `aria-label`（翻訳済み文字列）を付ける。図の中身（SVG）は
//   支援技術にとって意味を持たないため、`aria-label` が図の要約を担う。
//   **色だけで意味を持たせない**（INDEX 決定 21）——系列の区別は凡例のテキストが持つ。

export interface EChartProps {
  /** `dashboardCharts` の純関数が組んだ option。 */
  option: Record<string, unknown>;
  /** 図の要約（読み上げ用）。**翻訳済みの文字列**を渡す。 */
  ariaLabel: string;
  className?: string;
}

export function EChart({ option, ariaLabel, className }: EChartProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const chartRef = useRef<EChartsInstance | null>(null);

  useEffect(() => {
    let disposed = false;
    const el = containerRef.current;
    if (!el) return;

    void (async () => {
      try {
        const echarts = await loadECharts();
        if (disposed) return;
        chartRef.current ??= echarts.init(el, null, { renderer: 'svg' });
        chartRef.current.setOption(option);
      } catch (err) {
        // **投げない。** 図は補助であり、描けなくても表は残る。ここで送出すると
        // ErrorBoundary が画面ごと落とし、読めていた数値まで消える。
        console.error('chart rendering failed:', err);
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
      className={className ?? 'h-56 w-full'}
    />
  );
}
