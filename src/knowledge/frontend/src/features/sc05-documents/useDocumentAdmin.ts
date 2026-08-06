import { useQueryClient } from '@tanstack/react-query';
import {
  getBffDocumentContentQueryKey,
  getBffDocumentDetailQueryKey,
  getBffDocumentListQueryKey,
  getBffDocumentVersionsQueryKey,
  useBffDocumentArchive,
  useBffDocumentCreate,
  useBffDocumentDelete,
  useBffDocumentList,
  useBffDocumentPublish,
  useBffDocumentUpdate,
} from '@foundation/api/generated/documents/documents';
import { okArray } from '@foundation/api/orvalSelect';
import type { DocumentDto } from '@foundation/api/generated/bff.schemas';

// SC-05, UC-03, FR-06/FR-09: 文書管理の照会・作成・更新・状態遷移・削除
// （サーバー状態は TanStack Query。ADR-0031）。
//
// IADR-0135 決定 1（#519）: `/bff/documents` 群は **orval 生成フック**で呼ぶ
// （IADR-0127 決定 3 の「生成物が無いので apiFetch ＋ 手書き型」は #506 で契約が揃った時点で解消した）。
// IADR-0127 決定 5: 更新系の成功後は invalidateQueries だけを行う（手書きの再取得を持たない）。
// **無効化の対象は一覧だけではない**——生成キーでは一覧キーが SC-03 の詳細群に前方一致しないため、
// 当該文書の詳細・本文・版履歴も明示列挙する（IADR-0135 決定 3［2026-08-06 追記］）。
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
    query: { queryKey: documentsKey, select: okArray },
  });
}

/** 状態遷移・削除の種別。呼び分けを 1 箇所に閉じる。 */
export type DocumentCommand = 'publish' | 'archive' | 'delete';

/**
 * ある文書の変更が古くする問い合わせのキー（**SC-03 の 3 本を含む**）。
 *
 * IADR-0135 決定 3［2026-08-06 追記］: 生成キーは URL 1 要素（`['/bff/documents']` /
 * `['/bff/documents/{id}']` / `.../content` / `.../versions`）であり、TanStack Query の部分一致は
 * **配列の要素単位**なので、一覧キーは詳細群に**当たらない**。載せ替え前の階層キー
 * （`['bff','documents']` と `['bff','documents',id,…]`）が持っていた前方一致は成立しない。
 * SC-11 と同じく**明示列挙**して、SC-05 の更新が SC-03 へ届く状態を保つ。
 */
export function documentInvalidationKeys(id: string) {
  return [
    documentsKey,
    getBffDocumentDetailQueryKey(id),
    getBffDocumentContentQueryKey(id),
    getBffDocumentVersionsQueryKey(id),
  ];
}

/**
 * 作成・更新・状態遷移・削除をまとめて公開する。
 *
 * 成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない。IADR-0127 決定 5）。
 * 対象は**一覧に加えて当該文書の詳細・本文・版履歴**である（`documentInvalidationKeys`）。
 * 作成だけは対象 id が無く、まだキャッシュも無いので一覧のみを無効化する。
 */
export function useDocumentActions() {
  const queryClient = useQueryClient();
  const invalidateList = () => void queryClient.invalidateQueries({ queryKey: documentsKey });
  const invalidateDocument = (_data: unknown, variables: { id: string }) => {
    for (const queryKey of documentInvalidationKeys(variables.id)) {
      void queryClient.invalidateQueries({ queryKey });
    }
  };
  const onCreated = { mutation: { onSuccess: invalidateList } };
  const onChanged = { mutation: { onSuccess: invalidateDocument } };

  const create = useBffDocumentCreate<unknown>(onCreated);
  const update = useBffDocumentUpdate<unknown>(onChanged);
  const publish = useBffDocumentPublish<unknown>(onChanged);
  const archive = useBffDocumentArchive<unknown>(onChanged);
  const remove = useBffDocumentDelete<unknown>(onChanged);

  return { create, update, publish, archive, remove };
}
