import { describe, it, expect } from 'vitest';
import { searchTermBarOption, usageTrendLineOption } from './dashboardCharts';

// SC-10, UC-05, FR-10 / ADR-0031 §採用技術一覧（チャート = ECharts）/ #788:
// 図の「判断の部分」を純関数として固定する（jsdom では描画結果から検証できない）。

const labelFor = (t: string) => (t === 'search' ? '検索' : t === 'answer' ? 'AI 回答' : t);

describe('usageTrendLineOption', () => {
  // SC-10: 種別ごとに系列を分ける（1 本に混ぜると合計しか読めない）。
  it('creates one series per event type', () => {
    const option = usageTrendLineOption(
      [
        { date: '2026-08-02', eventType: 'search', count: 3 },
        { date: '2026-08-01', eventType: 'answer', count: 1 },
        { date: '2026-08-01', eventType: 'search', count: 2 },
      ],
      labelFor,
    );
    const series = option.series as { name: string; data: (number | null)[] }[];
    expect(series.map((s) => s.name)).toEqual(['AI 回答', '検索']);
  });

  // SC-10: 時系列は昇順。契約は順序を約束していないため、ここで並べ替える。
  it('sorts the x axis by date ascending', () => {
    const option = usageTrendLineOption(
      [
        { date: '2026-08-03', eventType: 'search', count: 1 },
        { date: '2026-08-01', eventType: 'search', count: 2 },
      ],
      labelFor,
    );
    expect((option.xAxis as { data: string[] }).data).toEqual(['2026-08-01', '2026-08-03']);
  });

  // SC-10: 欠測は 0 ではなく null。「その日は 0 件」と「点が無い」を混同させない。
  it('leaves gaps as null instead of zero', () => {
    const option = usageTrendLineOption(
      [
        { date: '2026-08-01', eventType: 'search', count: 2 },
        { date: '2026-08-02', eventType: 'answer', count: 5 },
      ],
      labelFor,
    );
    const series = option.series as { name: string; data: (number | null)[] }[];
    expect(series.find((s) => s.name === '検索')?.data).toEqual([2, null]);
    expect(series.find((s) => s.name === 'AI 回答')?.data).toEqual([null, 5]);
  });

  // SC-10: 期間内の利用が無くても option を組める（画面は表側で「利用はありません」を出す）。
  it('handles an empty period', () => {
    const option = usageTrendLineOption([], labelFor);
    expect((option.xAxis as { data: string[] }).data).toEqual([]);
    expect(option.series).toEqual([]);
  });

  // #788: 未知の種別を握り潰さない（契約が 2 値でもサーバが 3 つ目を返しうる）。
  it('keeps unknown event types as their own series', () => {
    const option = usageTrendLineOption(
      [{ date: '2026-08-01', eventType: 'unknown-kind', count: 1 }],
      labelFor,
    );
    expect((option.series as { name: string }[])[0].name).toBe('unknown-kind');
  });
});

describe('searchTermBarOption', () => {
  // SC-10: 上位語は件数の降順（順不同では「上位」が読めない）。
  it('sorts terms by count descending', () => {
    const option = searchTermBarOption(
      [
        { term: '経費', count: 4 },
        { term: '就業規則', count: 9 },
      ],
      '件数',
    );
    expect((option.xAxis as { data: string[] }).data).toEqual(['就業規則', '経費']);
    expect((option.series as { data: number[] }[])[0].data).toEqual([9, 4]);
  });

  // #788: 入力を破壊しない（呼び出し側の配列＝Query のキャッシュを並べ替えない）。
  it('does not mutate the input', () => {
    const terms = [
      { term: 'a', count: 1 },
      { term: 'b', count: 2 },
    ];
    searchTermBarOption(terms, '件数');
    expect(terms.map((t) => t.term)).toEqual(['a', 'b']);
  });
});
