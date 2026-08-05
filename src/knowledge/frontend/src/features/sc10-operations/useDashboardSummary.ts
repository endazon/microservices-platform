import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-10, UC-05, FR-10: 運用ダッシュボードのサマリ（サーバー状態は TanStack Query。ADR-0031）。
//
// BFF（/bff/dashboard/summary）が DashboardService（利用状況・検索傾向）と
// FeedbackService（回答品質）を 1 応答へ集約する。`days` は BFF が 1〜90 へ丸め、
// 両後段へ同じ値を渡す（期間の起点を揃えるため）。
//
// `/bff/dashboard` は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506 の射程）。手書き HTTP クライアントではない
// ——出口は foundation/api の 1 箇所に収束している（IADR-0121 決定 3）。

/** 日次利用状況の 1 点（`UsagePointDto`）。`eventType` は契約の 2 値（search / answer）。 */
export interface UsagePoint {
  date: string;
  eventType: string;
  count: number;
}

/** 検索傾向の 1 点（`SearchTrendDto`）。 */
export interface SearchTrend {
  term: string;
  count: number;
}

/** 回答品質（`FeedbackStatsDto`）。`satisfactionRate` は 0〜1。 */
export interface FeedbackStats {
  up: number;
  down: number;
  total: number;
  satisfactionRate: number;
}

/** BFF が返すサマリ（`DashboardSummaryDto`）。 */
export interface DashboardSummary {
  totalSearches: number;
  totalAnswers: number;
  usageTrend: UsagePoint[];
  topSearchTerms: SearchTrend[];
  quality: FeedbackStats;
}

/** 集計期間の選択肢（BFF の上限 90 に収まる範囲で計画の「日次・週・月」に対応する 3 値）。 */
export const DAYS_OPTIONS = [7, 30, 90] as const;

export type DaysOption = (typeof DAYS_OPTIONS)[number];

/** キャッシュキー。**期間を含める**——期間を変えると別の問い合わせになる。 */
export const dashboardSummaryKey = (days: DaysOption) =>
  ['bff', 'dashboard', 'summary', days] as const;

export function useDashboardSummary(days: DaysOption) {
  return useQuery({
    queryKey: dashboardSummaryKey(days),
    queryFn: () => apiFetch<DashboardSummary>(`/dashboard/summary?days=${days}`),
  });
}
