import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

// SC-18 (#917): グラフ描画の器。echarts は動的 import（echartsGraphLoader）1 本に閉じてあるので、
// モックもそこへ当てる（EChart.test.tsx と同じ構図）。
const mocks = vi.hoisted(() => ({
  loadGraphECharts: vi.fn(),
  setOption: vi.fn(),
  dispose: vi.fn(),
  handlers: new Map<string, (params: unknown) => void>(),
}));
vi.mock('../../../components/echartsGraphLoader', () => ({
  loadGraphECharts: mocks.loadGraphECharts,
}));

import { GraphCanvas } from './GraphCanvas';

const OPTION = { series: [] };

beforeEach(() => {
  mocks.setOption.mockReset();
  mocks.dispose.mockReset();
  mocks.handlers.clear();
  mocks.loadGraphECharts.mockReset();
  mocks.loadGraphECharts.mockResolvedValue({
    init: () => ({
      setOption: mocks.setOption,
      dispose: mocks.dispose,
      resize: () => {},
      on: (event: string, handler: (params: unknown) => void) => mocks.handlers.set(event, handler),
      dispatchAction: () => {},
    }),
  });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('GraphCanvas (SC-18)', () => {
  // 図の要約は読み上げに届く（SVG の中身は支援技術にとって意味を持たない）。
  it('labels the canvas for assistive technology', async () => {
    render(<GraphCanvas option={OPTION} ariaLabel="ナレッジグラフ" />);
    expect(screen.getByRole('img', { name: 'ナレッジグラフ' })).toBeInTheDocument();
  });

  it('applies the option once echarts has loaded', async () => {
    render(<GraphCanvas option={OPTION} ariaLabel="図" />);
    await waitFor(() => expect(mocks.setOption).toHaveBeenCalledWith(OPTION));
  });

  // SC-18 主要素 5: ノードのクリックが文書 ID つきで届く（サイドパネルの入口）。
  it('reports node clicks with the document id', async () => {
    const onNodeClick = vi.fn();
    render(<GraphCanvas option={OPTION} ariaLabel="図" onNodeClick={onNodeClick} />);
    await waitFor(() => expect(mocks.handlers.has('click')).toBe(true));

    mocks.handlers.get('click')!({ dataType: 'node', data: { id: 'doc-1' } });
    expect(onNodeClick).toHaveBeenCalledWith('doc-1');
  });

  // 陽性対照の対: 辺のクリックはノード選択にしない（dataType で選り分ける）。
  it('ignores clicks on edges', async () => {
    const onNodeClick = vi.fn();
    render(<GraphCanvas option={OPTION} ariaLabel="図" onNodeClick={onNodeClick} />);
    await waitFor(() => expect(mocks.handlers.has('click')).toBe(true));

    mocks.handlers.get('click')!({ dataType: 'edge', data: { id: 'e-1' } });
    expect(onNodeClick).not.toHaveBeenCalled();
  });

  // 描けなくても投げない —— 帯・凡例・パネルは器の外にあり、そこまで巻き込まない。
  it('does not throw when the chart fails to load', async () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
    mocks.loadGraphECharts.mockRejectedValue(new Error('chunk load failed'));
    render(<GraphCanvas option={OPTION} ariaLabel="図" />);
    await waitFor(() => expect(consoleError).toHaveBeenCalled());
    expect(screen.getByRole('img', { name: '図' })).toBeInTheDocument();
  });
});
