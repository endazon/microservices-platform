import {
  getBffDocumentContentQueryKey,
  getBffDocumentDetailQueryKey,
  getBffDocumentVersionsQueryKey,
  useBffDocumentContent,
  useBffDocumentDetail,
  useBffDocumentVersions,
} from '@foundation/api/generated/documents/documents';
import { okData } from '@foundation/api/orvalSelect';
import { ApiError } from '@foundation/api/ApiError';
import type {
  DocumentContentDto,
  DocumentDto,
  DocumentVersionDto,
} from '@foundation/api/generated/bff.schemas';

// SC-03, UC-01/UC-02/UC-07, FR-05/FR-06/FR-12: 文書詳細のデータ取得（/bff/documents/*）。
// IADR-0126 決定 4: キーは BFF のパスに対応させ、**版履歴は詳細の成功後にだけ有効化**する
// （詳細が 404〔存在秘匿〕の文書に対して版履歴を叩いても、BFF は同じ 404 を返す＝確実に失敗する往復）。
//
// IADR-0135 決定 1（#519）: 3 本とも **orval 生成フック**で呼ぶ。キーは生成キー
// （`['/bff/documents/{id}']` 等）になり、「キーは BFF のパスに対応する」という上の性質は保たれる。

/** 404 は「不在」と「権限による秘匿」を区別しない（IADR-0009）。画面はどちらも中立に表示する。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

export function useDocumentQueries(id: string) {
  const detail = useBffDocumentDetail<DocumentDto, unknown>(id, {
    query: {
      queryKey: getBffDocumentDetailQueryKey(id),
      select: okData,
      enabled: id.length > 0,
    },
  });

  // 本文は詳細と疎結合に取得する（取得不能でも本体表示は続ける）。
  const content = useBffDocumentContent<DocumentContentDto, unknown>(id, {
    query: {
      queryKey: getBffDocumentContentQueryKey(id),
      select: okData,
      enabled: id.length > 0,
    },
  });

  const versions = useBffDocumentVersions<DocumentVersionDto[], unknown>(id, {
    query: {
      queryKey: getBffDocumentVersionsQueryKey(id),
      // `?? []` は残す（IADR-0132 決定 3）。契約上は必須でも、実行時に本文を検証する層は無い。
      select: (res) => okData(res) ?? [],
      enabled: detail.isSuccess,
    },
  });

  return { detail, content, versions };
}
