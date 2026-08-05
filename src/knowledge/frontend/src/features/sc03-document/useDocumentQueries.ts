import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import { ApiError } from '@foundation/api/ApiError';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細のデータ取得（/bff/documents/*）。
// IADR-0126 決定 4: キーは BFF のパスに対応させ、**版履歴は詳細の成功後にだけ有効化**する
// （詳細が 404〔存在秘匿〕の文書に対して版履歴を叩いても、BFF は同じ 404 を返す＝確実に失敗する往復）。

export interface DocumentDetail {
  id: string;
  title: string;
  status: string;
  markdownUri?: string | null;
  version: number;
  attributes: Record<string, string>;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

export interface DocumentContent {
  id: string;
  title: string;
  markdown: string;
  sourceUri?: string | null;
}

export interface DocumentVersion {
  documentId: string;
  version: number;
  title: string;
  status: string;
  changeNote?: string | null;
  createdAt: string;
}

/** 404 は「不在」と「権限による秘匿」を区別しない（IADR-0009）。画面はどちらも中立に表示する。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

export function useDocumentQueries(id: string) {
  const detail = useQuery({
    queryKey: ['bff', 'documents', id],
    queryFn: () => apiFetch<DocumentDetail>(`/documents/${id}`),
    enabled: id.length > 0,
  });

  // 本文は詳細と疎結合に取得する（取得不能でも本体表示は続ける）。
  const content = useQuery({
    queryKey: ['bff', 'documents', id, 'content'],
    queryFn: () => apiFetch<DocumentContent>(`/documents/${id}/content`),
    enabled: id.length > 0,
  });

  const versions = useQuery({
    queryKey: ['bff', 'documents', id, 'versions'],
    queryFn: async () => (await apiFetch<DocumentVersion[]>(`/documents/${id}/versions`)) ?? [],
    enabled: detail.isSuccess,
  });

  return { detail, content, versions };
}
