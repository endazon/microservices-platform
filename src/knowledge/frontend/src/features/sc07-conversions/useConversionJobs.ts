import { useQueryClient } from '@tanstack/react-query';
import {
  getBffConversionJobListQueryKey,
  useBffConversionJobList,
  useBffConversionJobRetry,
} from '@foundation/api/generated/conversion/conversion';
import { okArray } from '@foundation/api/orvalSelect';
import type { ConversionJobDto } from '@foundation/api/generated/bff.schemas';
import type { JobStatusFilter } from './jobStatus';

// SC-07, UC-06, FR-12: 変換ジョブの照会・再変換（サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0135 決定 1（#519）: `/bff/conversion/jobs` 群は **orval 生成フック**で呼ぶ
// （IADR-0127 決定 3 の「生成物が無いので apiFetch ＋ 手書き型」は #506 で契約が揃った時点で解消した）。
// 絞り込みは URL の手組みではなく**クエリパラメータ**として渡す（`?status=` の組み立ては生成コードが持つ）。
//
// IADR-0127 決定 5: 再変換の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

/** 絞り込み条件をクエリパラメータへ写す。空文字（すべて）は**パラメータ自体を送らない**。 */
function listParams(status: JobStatusFilter) {
  return status ? { status } : undefined;
}

/** キャッシュキー。絞り込み条件を含めるため、条件を変えると別の問い合わせになる。 */
export const conversionJobsKey = (status: JobStatusFilter) =>
  getBffConversionJobListQueryKey(listParams(status));

/**
 * ジョブ一覧（UC-06 代替フロー「変換ジョブの状況を照会する」）。
 *
 * 計画確定の照会 API は「`GET /jobs` 相当。**状態でのフィルタを備える**（「失敗のみ」フィルタの実体）」。
 */
export function useConversionJobs(status: JobStatusFilter) {
  return useBffConversionJobList<ConversionJobDto[], unknown>(listParams(status), {
    query: { queryKey: conversionJobsKey(status), select: okArray },
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
  return useBffConversionJobRetry<unknown>({
    mutation: {
      // 一覧のキーは絞り込み条件を含むため、条件を問わず束ごと無効化する
      // （再変換で `failed` → `queued` へ動くと、どの条件の一覧も古くなるため）。
      // **引数なしの生成キー（`['/bff/conversion/jobs']`）は条件つきキーの前方一致になる**
      // ——TanStack Query の部分一致は配列の要素単位であり、条件は 2 要素目に載る（IADR-0135 決定 3）。
      onSuccess: () =>
        void queryClient.invalidateQueries({ queryKey: getBffConversionJobListQueryKey() }),
    },
  });
}
