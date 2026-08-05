import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-05, UC-03, FR-06/FR-09: 文書管理の照会・作成・更新・状態遷移・削除
// （サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0127 決定 3: `/bff/documents` は docs/api/openapi.yaml に無く orval 生成物が存在しないため、
// `apiFetch` ＋ 本ファイルの手書き型で呼ぶ（#506）。
// IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
//
// 書き込みは BFF が「対象文書が利用者の ABAC スコープ内か」を先に確かめ、スコープ外・不在を
// いずれも 404 で返す（IADR-0041 / IADR-0009。閲覧できない文書は変更もできない）。

/** 文書 1 件（BFF の `DocumentDto` に対応）。 */
export interface AdminDocument {
  id: string;
  title: string;
  /** 公開ライフサイクル（`draft` / `normalized` / `published` / `archived`）。**変換結果ではない。** */
  status: string;
  markdownUri?: string | null;
  version: number;
  attributes: Record<string, string>;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

export interface CreateDocumentInput {
  title: string;
  attributes: Record<string, string>;
  tags: string[];
}

export interface UpdateDocumentInput extends CreateDocumentInput {
  id: string;
  /** 楽観的並行制御。現在版と不一致ならサーバが 409 を返す（FR-06 のバージョン管理）。 */
  expectedVersion: number;
  changeNote: string | null;
}

export const documentsKey = ['bff', 'documents'] as const;

/** 一覧（ABAC スコープ内のみ返る）。 */
export function useAdminDocuments() {
  return useQuery({
    queryKey: documentsKey,
    queryFn: () => apiFetch<AdminDocument[]>('/documents'),
  });
}

/** 状態遷移・削除の種別。パスの組み立てを 1 箇所に閉じる。 */
export type DocumentCommand = 'publish' | 'archive' | 'delete';

/** 作成・更新・状態遷移・削除をまとめて公開する（成功後は一覧を無効化する）。 */
export function useDocumentActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: documentsKey });

  const create = useMutation({
    mutationFn: (input: CreateDocumentInput) =>
      apiFetch<AdminDocument>('/documents', { method: 'POST', json: input }),
    onSuccess: invalidate,
  });

  const update = useMutation({
    mutationFn: ({ id, ...rest }: UpdateDocumentInput) =>
      apiFetch(`/documents/${id}`, { method: 'PUT', json: rest }),
    onSuccess: invalidate,
  });

  const command = useMutation({
    mutationFn: ({ id, kind }: { id: string; kind: DocumentCommand }) =>
      kind === 'delete'
        ? apiFetch(`/documents/${id}`, { method: 'DELETE' })
        : apiFetch(`/documents/${id}/${kind}`, { method: 'POST' }),
    onSuccess: invalidate,
  });

  return { create, update, command };
}
