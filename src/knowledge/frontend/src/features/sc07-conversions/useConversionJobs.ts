import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  getBffConversionJobFigureImageQueryKey,
  getBffConversionJobFiguresQueryKey,
  getBffConversionJobListQueryKey,
  useBffConversionJobFigureCorrection,
  useBffConversionJobFigureImage,
  useBffConversionJobFigures,
  useBffConversionJobList,
  useBffConversionJobRetry,
} from '@foundation/api/generated/conversion/conversion';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type { ConversionFigureDto, ConversionJobDto } from '@foundation/api/generated/bff.schemas';
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
 * ジョブの図の一覧（2 ペインの材料。#651 / [[IADR-0154]] 決定 2）。
 *
 * `jobId` が `null` のあいだは**問い合わせない**——2 ペインは行操作で開くものであり、
 * 一覧を描くたびにジョブ数ぶんの図取得が飛ぶのは無駄である。
 *
 * **照会（GET）だが管理者限定**である（IADR-0154 決定 6）。2 ペインを開く操作そのものが
 * 人手補正だからであり、ジョブ一覧の照会（admin ＋ operator）とは実効ロールが違う。
 */
export function useJobFigures(jobId: string | null) {
  return useBffConversionJobFigures<ConversionFigureDto[], unknown>(jobId ?? '', {
    query: {
      queryKey: getBffConversionJobFiguresQueryKey(jobId ?? ''),
      enabled: jobId !== null,
      select: okArray,
    },
  });
}

/**
 * 縮退した図の元画像を**オブジェクト URL**として返す（2 ペインの右側）。
 *
 * `imageUri`（`storage://…`）を `<img src>` に入れることはできない——ブラウザから解決できず、
 * 取得には Bearer が要るためである（[[IADR-0154]] 決定 2）。**BFF から Blob で受け取り、
 * `URL.createObjectURL` で包む。**
 *
 * **［#651］この口は `docs/api/openapi.yaml` 唯一の非 JSON 応答である。** 生成フックの宣言型は
 * `data: Blob` だが、`foundation/api/orvalMutator.ts` の `bffFetch` が Content-Type を見ずに
 * `JSON.parse` していたため**生成時点では実行不能**だった。同ファイルを直したうえで使っている。
 *
 * **オブジェクト URL は必ず解放する**（`revokeObjectURL`）。図を切り替えるたびに作られるため、
 * 解放しないとパネルを開き閉じするだけで blob が溜まる。
 */
export function useFigureImageUrl(jobId: string | null, figureId: string | null) {
  const query = useBffConversionJobFigureImage<Blob, unknown>(jobId ?? '', figureId ?? '', {
    query: {
      queryKey: getBffConversionJobFigureImageQueryKey(jobId ?? '', figureId ?? ''),
      enabled: jobId !== null && figureId !== null,
      select: okData,
      // 画像が無い図（コード化済み・ストレージ未解決）は 404 で確定する。既定の再試行は
      // 「無い」ことの確認を 3 回繰り返すだけなので切る。
      retry: false,
    },
  });
  const blob = query.data ?? null;
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!blob) {
      setUrl(null);
      return;
    }
    // jsdom は createObjectURL を実装しない。**画面テストで落とさないために握り潰すのではなく**、
    // 未実装なら URL を作らない（右ペインは「画像を表示できません」の側へ倒れる）。
    if (typeof URL.createObjectURL !== 'function') return;
    const objectUrl = URL.createObjectURL(blob);
    setUrl(objectUrl);
    return () => {
      URL.revokeObjectURL(objectUrl);
      setUrl(null);
    };
  }, [blob]);

  return { url, isPending: query.isPending, isError: query.isError };
}

/**
 * 人手補正の投稿（Phase 1 = 図のコード片。#651 / [[IADR-0154]] 決定 3）。
 *
 * 成功後は**一覧と図の両方**を無効化する——本文の差し替えで `hasCorrection` /
 * `diagramsRetained` が動き、図側の `corrected` も変わるためである。
 */
export function useCorrectFigure() {
  const queryClient = useQueryClient();
  return useBffConversionJobFigureCorrection<unknown>({
    mutation: {
      onSuccess: () => {
        void queryClient.invalidateQueries({ queryKey: getBffConversionJobListQueryKey() });
        // 図の一覧のキーは `/bff/conversion/jobs/{id}/figures`。ジョブ一覧のキー
        // （`/bff/conversion/jobs`）とは別なので、束ごとの無効化では届かない。
        void queryClient.invalidateQueries({
          predicate: (q) => String(q.queryKey[0]).includes('/figures'),
        });
      },
    },
  });
}

/**
 * 再変換（UC-06 代替フロー「失敗した変換を再実行する」）。
 *
 * 計画確定: 「手動再変換の回数上限は設けない。ただし**同一ジョブの再変換は直列化**し、
 * 実行中（`processing`）の再変換要求は拒否する」。サーバは `failed` 以外を **409（`not_retryable`）**
 * で拒否するため、画面は 409 を「拒否された」として扱う（呼び出し側で文言を選ぶ）。
 *
 * **［#651］409 はもう 1 種類ある** —— `corrections_would_be_lost`（人手補正が入ったジョブ）。
 * 呼び出し側は本文の `error` で出し分け、確認後に `params: { discardCorrections: true }` で再送する。
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

/**
 * 本画面のミューテーション一式（#651）。
 *
 * **[[IADR-0127]] 決定 7 のための束**である。画面は「直近の操作の結果だけ」を出す必要があり、
 * その列挙を `Object.values(actions)` で導く——**手書きの配列にすると 3 本目を足したときに
 * 同じ穴が空く**（SC-06 `useDataSourceActions()` と同じ形）。
 *
 * `ConversionJobsPage.tsx` は #503 以来「ミューテーションは retry の 1 本だけ」であり、
 * そのコメント自身が「**2 本目を足すときは SC-05 / SC-06 の `beginOperation()` と同じ形へ
 * 移すこと**」と予告していた。人手補正の投稿がその 2 本目にあたるので、ここで移す。
 */
export function useConversionJobActions() {
  const retry = useRetryConversionJob();
  const correct = useCorrectFigure();
  return { retry, correct };
}
