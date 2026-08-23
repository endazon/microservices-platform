import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

// ADR-0031 §採用技術一覧（チャート = Apache ECharts）/ NFR・IADR-0134（初期ロード予算）/ #788:
// **echarts は動的 import で読む**（静的 import すると初期ロードへ載る）。読み込み口を
// `echartsLoader` 1 本に閉じてあるので、モックもそこへ当てられる。
const mocks = vi.hoisted(() => ({
  loadECharts: vi.fn(),
  setOption: vi.fn(),
  dispose: vi.fn(),
}));
vi.mock('./echartsLoader', () => ({ loadECharts: mocks.loadECharts }));

import { EChart } from './EChart';

const OPTION = { series: [] };

beforeEach(() => {
  mocks.setOption.mockReset();
  mocks.dispose.mockReset();
  mocks.loadECharts.mockReset();
  mocks.loadECharts.mockResolvedValue({
    init: () => ({ setOption: mocks.setOption, dispose: mocks.dispose, resize: () => {} }),
  });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('EChart', () => {
  // #788: 図の要約は読み上げに届く（SVG の中身は支援技術にとって意味を持たない）。
  it('labels the chart container for assistive technology', async () => {
    render(<EChart option={OPTION} ariaLabel="利用状況の推移グラフ" />);
    expect(screen.getByRole('img', { name: '利用状況の推移グラフ' })).toBeInTheDocument();
  });

  // #788: option は読み込み完了後に反映される。
  it('applies the option once echarts has loaded', async () => {
    render(<EChart option={OPTION} ariaLabel="図" />);
    await waitFor(() => expect(mocks.setOption).toHaveBeenCalledWith(OPTION));
  });

  // #788: **図は補助であり、描けなくても投げない。** 投げると ErrorBoundary が画面ごと落とし、
  // 表で読めていた数値まで消える。
  it('does not throw when the chart fails to load', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    mocks.loadECharts.mockRejectedValue(new Error('chunk load failed'));
    render(<EChart option={OPTION} ariaLabel="図" />);
    // 器は残り、読み上げのラベルも失われない。
    expect(screen.getByRole('img', { name: '図' })).toBeInTheDocument();
    await waitFor(() => expect(console.error).toHaveBeenCalled());
  });

  // #788: 破棄でインスタンスを解放する（画面を行き来するたびに漏らさない）。
  it('disposes the chart on unmount', async () => {
    const { unmount } = render(<EChart option={OPTION} ariaLabel="図" />);
    await waitFor(() => expect(mocks.setOption).toHaveBeenCalled());
    unmount();
    expect(mocks.dispose).toHaveBeenCalled();
  });
});
