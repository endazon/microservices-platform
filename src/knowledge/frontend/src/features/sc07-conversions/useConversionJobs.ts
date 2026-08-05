import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import type { JobStatusFilter } from './jobStatus';

// SC-07, UC-06, FR-12: 変換ジョブの照会・再変換（サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0127 決定 3: `/bff/conversion/jobs` は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506 の射程を広げる）。手書き HTTP クライアントではない
// ——出口は foundation/api の 1 箇所に収束している（IADR-0121 決定 3）。
//
// IADR-0127 決定 5: 再変換の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

/** 変換ジョブ 1 件（BFF の `ConversionJobDto` に対応）。 */
export interface ConversionJob {
  id: string;
  sourceId: string;
  sourceType: string;
  originalPath: string;
  /** 計画確定の 4 値（`queued` / `processing` / `succeeded` / `failed`）。未知の値も受け取る。 */
  status: string;
  error?: string | null;
  documentId?: string | null;
  markdownUri?: string | null;
  attempts: number;
  createdAt: string;
  updatedAt: string;
}

/** キャッシュキー。絞り込み条件を含めるため、条件を変えると別の問い合わせになる。 */
export const conversionJobsKey = (status: JobStatusFilter) =>
  ['bff', 'conversion', 'jobs', status] as const;

/**
 * ジョブ一覧（UC-06 代替フロー「変換ジョブの状況を照会する」）。
 *
 * 計画確定の照会 API は「`GET /jobs` 相当。**状態でのフィルタを備える**（「失敗のみ」フィルタの実体）」。
 */
export function useConversionJobs(status: JobStatusFilter) {
  return useQuery({
    queryKey: conversionJobsKey(status),
    queryFn: () =>
      apiFetch<ConversionJob[]>(
        status ? `/conversion/jobs?status=${encodeURIComponent(status)}` : '/conversion/jobs',
      ),
  });
}

/**
 * 再変換（UC-06 代替フロー「失敗した変換を再実行する」）。
 *
 * 計画確定: 「手動再変換の回数上限は設けない。ただし**同一ジョブの再変換は直列化**し、
 * 実行中（`processing`）の再変換要求は拒否する」。サーバは `failed` 以外を **409（`not_retryable`）**
 * で拒否するため、画面は 409 を「拒否された」として扱う（呼び出し側で文言を選ぶ）。
 */
export function useRetryConversionJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiFetch(`/conversion/jobs/${id}/retry`, { method: 'POST' }),
    // 一覧のキーは絞り込み条件を含むため、条件を問わず束ごと無効化する
    // （再変換で `failed` → `queued` へ動くと、どの条件の一覧も古くなるため）。
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['bff', 'conversion', 'jobs'] }),
  });
}
