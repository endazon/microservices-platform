import { useQueryClient } from '@tanstack/react-query';
import {
  getBffDocumentListQueryKey,
  useBffDocumentArchive,
  useBffDocumentCreate,
  useBffDocumentDelete,
  useBffDocumentList,
  useBffDocumentPublish,
  useBffDocumentUpdate,
} from '@foundation/api/generated/documents/documents';
import { okData } from '@foundation/api/orvalSelect';
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';

// SC-05, UC-03, FR-06/FR-09: 文書管理の照会・作成・更新・状態遷移・削除
// （サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0135 決定 1（#519）: `/bff/documents` 群は **orval 生成フック**で呼ぶ
// （IADR-0127 決定 3 の「生成物が無いので apiFetch ＋ 手書き型」は #506 で契約が揃った時点で解消した）。
// IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
//
// **状態遷移は 1 本の `useMutation` の分岐ではなく 3 本の生成フックになる**（publish / archive / delete）。
// 画面はそれを `DocumentCommand` で選ぶ（IADR-0135 決定 6）。
//
// 書き込みは BFF が「対象文書が利用者の ABAC スコープ内か」を先に確かめ、スコープ外・不在を
// いずれも 404 で返す（IADR-0041 / IADR-0009。閲覧できない文書は変更もできない）。

export const documentsKey = getBffDocumentListQueryKey();

/** 一覧（ABAC スコープ内のみ返る）。 */
export function useAdminDocuments() {
  return useBffDocumentList<DocumentDto[], unknown>({
    query: { queryKey: documentsKey, select: okData },
  });
}

/** 状態遷移・削除の種別。呼び分けを 1 箇所に閉じる。 */
export type DocumentCommand = 'publish' | 'archive' | 'delete';

/** 作成・更新・状態遷移・削除をまとめて公開する（成功後は一覧を無効化する）。 */
export function useDocumentActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: documentsKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffDocumentCreate<unknown>(onSuccess);
  const update = useBffDocumentUpdate<unknown>(onSuccess);
  const publish = useBffDocumentPublish<unknown>(onSuccess);
  const archive = useBffDocumentArchive<unknown>(onSuccess);
  const remove = useBffDocumentDelete<unknown>(onSuccess);

  return { create, update, publish, archive, remove };
}
