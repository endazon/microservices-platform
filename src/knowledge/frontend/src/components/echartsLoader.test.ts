import { describe, it, expect, beforeEach } from 'vitest';
import { loadECharts, resetEChartsCacheForTest } from './echartsLoader';

// SC-10, FR-10 / ADR-0031 §採用技術一覧「チャート = Apache ECharts（自己ホスト）」/ #788:
// **読み込み層そのもの**を検証する。`EChart.test.tsx` はこのモジュールを `vi.mock` で丸ごと
// 差し替えるため、実体（動的 import・登録・キャッシュ）は一度も走らない。
// ここは**モックせずに**通し、実際に解決できること・二度読みしないことを固定する。
describe('loadECharts（遅延読み込みと 1 度きりの解決）', () => {
  beforeEach(() => {
    resetEChartsCacheForTest();
  });

  // SC-10: 登録済みの echarts が解決でき、描画に要る面（init）を持つ。
  it('解決した module は init を持つ', async () => {
    const echarts = await loadECharts();

    expect(typeof echarts.init).toBe('function');
  });

  // SC-10: 画面を行き来するたびに import を走らせない（同一の Promise を返す）。
  it('二度目の呼び出しは同じ解決結果を返す', async () => {
    const first = await loadECharts();
    const second = await loadECharts();

    expect(second).toBe(first);
  });

  // SC-10: キャッシュを捨てたら読み直す（テスト間の独立を担保する口が実際に効く）。
  it('キャッシュを捨てると読み直す', async () => {
    const first = await loadECharts();
    resetEChartsCacheForTest();
    const afterReset = await loadECharts();

    // 同じモジュール実体が返る（登録は冪等）。読み直しても壊れないことを見る。
    expect(typeof afterReset.init).toBe('function');
    expect(afterReset).toBe(first);
  });
});
