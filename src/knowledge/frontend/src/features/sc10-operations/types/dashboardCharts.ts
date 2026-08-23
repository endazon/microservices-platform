/**
 * 図が読む形。**契約の DTO（`UsagePointDto` / `SearchTrendDto`）をそのまま受けない。**
 * `eventType` は生成型では 2 値の union だが、**サーバが 3 つ目を返しても図は落ちてはならない**
 * （画面側 `usageEventLabel` も未知の値を握り潰さない方針である）。構造だけを要求する形にすると、
 * DTO はそのまま渡せて、未知の値も表現できる。
 */
export interface UsagePointLike {
  date: string;
  eventType: string;
  count: number;
}

export interface SearchTermLike {
  term: string;
  count: number;
}

// SC-10, UC-05, FR-10 / ADR-0031 §採用技術一覧「チャート = Apache ECharts」/
// IADR-0121 決定 1 の第 4 段（#788）。
//
// **option の組み立ては純関数として切り出す。** 描画（`echarts.init`）と混ぜると、
// 「どの系列が出るか」「日付が昇順か」といった**判断の部分がテストできなくなる**
// （jsdom には canvas が無く、描画結果からは検証できない）。
//
// **表示文言はここに書かない。** 呼び出し側が翻訳済みの文字列を渡す。ここへ書くと
// Lingui の抽出対象から外れ、カタログの網羅検査（IADR-0125 決定 4）を素通りする。
//
// **色を明示しない。** 系列色は ECharts の既定に任せ、意味は**凡例のテキスト**が持つ
// （INDEX 決定 21「色だけで意味を持たせない」）。

/** ECharts の `setOption` へ渡す最小の型。echarts の型を持ち込まない（純関数を描画から独立に保つ）。 */
export type ChartOption = Record<string, unknown>;

/** 利用状況（日次）の折れ線。**種別ごとに系列を分ける**——2 種別を 1 本に混ぜると合計しか読めない。 */
export function usageTrendLineOption(
  points: readonly UsagePointLike[],
  labelFor: (eventType: string) => string,
): ChartOption {
  // 日付は昇順（契約は順序を約束していない。時系列の図で順序が崩れると読めない）。
  const dates = [...new Set(points.map((p) => p.date))].sort();
  const eventTypes = [...new Set(points.map((p) => p.eventType))].sort();

  const series = eventTypes.map((eventType) => ({
    name: labelFor(eventType),
    type: 'line',
    // 欠測は 0 ではなく null（「その日は 0 件だった」と「点が無い」を混同させない）。
    data: dates.map(
      (date) => points.find((p) => p.date === date && p.eventType === eventType)?.count ?? null,
    ),
  }));

  return {
    tooltip: { trigger: 'axis' },
    legend: { data: eventTypes.map(labelFor) },
    grid: { left: 40, right: 12, top: 32, bottom: 24 },
    xAxis: { type: 'category', data: dates },
    yAxis: { type: 'value', minInterval: 1 },
    series,
  };
}

/** 検索傾向（上位語）の棒グラフ。件数の降順に並べる（上位語の図で順不同は読めない）。 */
export function searchTermBarOption(
  terms: readonly SearchTermLike[],
  seriesName: string,
): ChartOption {
  const sorted = [...terms].sort((a, b) => b.count - a.count);
  return {
    tooltip: { trigger: 'axis' },
    grid: { left: 40, right: 12, top: 32, bottom: 24 },
    xAxis: { type: 'category', data: sorted.map((t) => t.term) },
    yAxis: { type: 'value', minInterval: 1 },
    series: [{ name: seriesName, type: 'bar', data: sorted.map((t) => t.count) }],
  };
}
