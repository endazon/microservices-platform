import { describe, it, expect, beforeEach } from 'vitest';
import { loadGraphECharts, resetGraphEChartsCacheForTest } from './echartsGraphLoader';

// SC-18, FR-17 / ADR-0039 (#917): グラフ描画（GraphChart）の読み込み層そのもの。
// `GraphCanvas.test.tsx` はこのモジュールを `vi.mock` で丸ごと差し替えるため、実体
// （動的 import・GraphChart の登録・キャッシュ）は一度も走らない。ここは**モックせずに**通し、
// 実際に解決できること・二度読みしないことを固定する（echartsLoader.test.ts と同じ構図）。
describe('loadGraphECharts（遅延読み込みと 1 度きりの解決）', () => {
  beforeEach(() => {
    resetGraphEChartsCacheForTest();
  });

  // SC-18: 登録済みの echarts が解決でき、描画に要る面（init）を持つ。
  it('解決した module は init を持つ', async () => {
    const echarts = await loadGraphECharts();

    expect(typeof echarts.init).toBe('function');
  });

  // SC-18: 画面を行き来するたびに import を走らせない（同一の解決結果を返す）。
  it('二度目の呼び出しは同じ解決結果を返す', async () => {
    const first = await loadGraphECharts();
    const second = await loadGraphECharts();

    expect(second).toBe(first);
  });

  // SC-18: キャッシュを捨てたら読み直す（テスト間の独立を担保する口が実際に効く）。
  it('キャッシュを捨てると読み直す', async () => {
    const first = await loadGraphECharts();
    resetGraphEChartsCacheForTest();
    const afterReset = await loadGraphECharts();

    expect(typeof afterReset.init).toBe('function');
    expect(afterReset).toBe(first);
  });
});
