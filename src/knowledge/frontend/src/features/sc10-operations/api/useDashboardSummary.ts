import {
  getBffDashboardSummaryQueryKey,
  useBffDashboardSummary as useGeneratedDashboardSummary,
} from '@foundation/api/generated/dashboard/dashboard';
import { okData } from '@foundation/api/orvalSelect';
import type { DashboardSummaryDto } from '@foundation/api/generated/bff.schemas';

// SC-10, UC-05, FR-10: 運用ダッシュボードのサマリ（サーバー状態は TanStack Query。ADR-0031）。
//
// BFF（/bff/dashboard/summary）が DashboardService（利用状況・検索傾向）と
// FeedbackService（回答品質）を 1 応答へ集約する。`days` は BFF が 1〜90 へ丸め、
// 両後段へ同じ値を渡す（期間の起点を揃えるため）。
//
// IADR-0135 決定 1（#519）: **orval 生成フック**で呼ぶ。`/bff/dashboard/summary` は
// docs/api/openapi.yaml に**在る**——載せ替え前のコメントは「無く」と書いていたが誤りであった
// （#506 §実測 4 が指摘。#504 の作業仕様書 §6 の記述がそのまま写されていた）。
// 期間は URL の組み立てではなく**クエリパラメータ**として渡す（`?days=` の生成は生成コードが行う）。

/** 集計期間の選択肢（BFF の上限 90 に収まる範囲で計画の「日次・週・月」に対応する 3 値）。 */
export const DAYS_OPTIONS = [7, 30, 90] as const;

export type DaysOption = (typeof DAYS_OPTIONS)[number];

/** キャッシュキー。**期間を含める**——期間を変えると別の問い合わせになる。 */
export const dashboardSummaryKey = (days: DaysOption) => getBffDashboardSummaryQueryKey({ days });

export function useDashboardSummary(days: DaysOption) {
  return useGeneratedDashboardSummary<DashboardSummaryDto, unknown>(
    { days },
    { query: { queryKey: dashboardSummaryKey(days), select: okData } },
  );
}
