import { useQueryClient } from '@tanstack/react-query';
import {
  getBffDataSourceListQueryKey,
  useBffDataSourceCreate,
  useBffDataSourceDelete,
  useBffDataSourceList,
  useBffDataSourceSync,
} from '@foundation/api/generated/data-sources/data-sources';
import { okData } from '@foundation/api/orvalSelect';
import type { DataSourceDto } from '@foundation/api/generated/bff.schemas';

// SC-06, UC-04, FR-01/FR-02: データソースの照会・登録・手動同期・無効化
// （サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0135 決定 1（#519）: `/bff/datasources` 群は **orval 生成フック**で呼ぶ
// （IADR-0127 決定 3 の「生成物が無いので apiFetch ＋ 手書き型」は #506 で契約が揃った時点で解消した）。
// IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

export const dataSourcesKey = getBffDataSourceListQueryKey();

/** 一覧。BFF は後段障害を空一覧へ縮退させない（502）ので、失敗はそのままエラーとして扱う。 */
export function useDataSources() {
  return useBffDataSourceList<DataSourceDto[], unknown>({
    query: { queryKey: dataSourcesKey, select: okData },
  });
}

/** 登録・手動同期・無効化の 3 操作をまとめて公開する（成功後は一覧を無効化する）。 */
export function useDataSourceActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: dataSourcesKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffDataSourceCreate<unknown>(onSuccess);
  // UC-04 代替フロー: 手動同期を実行する。後段は 202 Accepted を返す（取得はポーリングジョブが行う）。
  const sync = useBffDataSourceSync<unknown>(onSuccess);
  const disable = useBffDataSourceDelete<unknown>(onSuccess);

  return { create, sync, disable };
}
