import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-06, UC-04, FR-01/FR-02: データソースの照会・登録・手動同期・無効化
// （サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0127 決定 3: `/bff/datasources` は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506 の射程を広げる）。
// IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。

/** データソース 1 件（BFF の `DataSourceDto` に対応）。 */
export interface DataSource {
  id: string;
  name: string;
  sourceType: string;
  connectionUri: string;
  /** `active` / `disabled` の 2 値（`DataSourceStatus`）。同期の健全性は表さない。 */
  status: string;
  lastSyncedAt?: string | null;
  config: Record<string, string>;
  defaultAttributes: Record<string, string>;
  createdAt: string;
}

/** 登録要求（BFF の `CreateDataSourceRequest` に対応）。 */
export interface CreateDataSourceInput {
  name: string;
  sourceType: string;
  connectionUri: string;
  defaultAttributes: Record<string, string>;
}

export const dataSourcesKey = ['bff', 'datasources'] as const;

/** 一覧。BFF は後段障害を空一覧へ縮退させない（502）ので、失敗はそのままエラーとして扱う。 */
export function useDataSources() {
  return useQuery({
    queryKey: dataSourcesKey,
    queryFn: () => apiFetch<DataSource[]>('/datasources'),
  });
}

/** 登録・手動同期・無効化の 3 操作をまとめて公開する（成功後は一覧を無効化する）。 */
export function useDataSourceActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: dataSourcesKey });

  const create = useMutation({
    mutationFn: (input: CreateDataSourceInput) =>
      apiFetch<DataSource>('/datasources', { method: 'POST', json: input }),
    onSuccess: invalidate,
  });

  // UC-04 代替フロー: 手動同期を実行する。後段は 202 Accepted を返す（取得はポーリングジョブが行う）。
  const sync = useMutation({
    mutationFn: (id: string) => apiFetch(`/datasources/${id}/sync`, { method: 'POST' }),
    onSuccess: invalidate,
  });

  const disable = useMutation({
    mutationFn: (id: string) => apiFetch(`/datasources/${id}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });

  return { create, sync, disable };
}
