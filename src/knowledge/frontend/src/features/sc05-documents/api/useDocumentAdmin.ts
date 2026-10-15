import { keepPreviousData, useQueryClient } from '@tanstack/react-query';
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
import { okData } from '@foundation/api/orvalSelect';
import type {
  BffDocumentListParams,
  BffDocumentListSort,
  BffDocumentListStatus,
  DocumentListPageDto,
} from '@foundation/api/generated/bff.schemas';

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

/**
 * 一覧の全ページに前方一致するキー（`['/bff/documents']`）。
 *
 * ［2026-10-14 / #1923］一覧はページ（`offset` / `limit`）ごとに `['/bff/documents', { offset, limit }]` で引く。
 * TanStack Query の部分一致は配列の要素単位なので、このキーでの無効化は**全ページ**に届く（詳細群には届かない。下記）。
 */
export const documentsKey = getBffDocumentListQueryKey();

/** SC-05, FR-06, IADR-0537（#1923）: 1 ページの件数の選択肢と既定（BFF の上限 100・既定 50 と同じ）。 */
export const DOCUMENT_PAGE_SIZES = [25, 50, 100] as const;
export const DEFAULT_DOCUMENT_PAGE_SIZE = 50;

export interface DocumentPageRequest {
  offset: number;
  limit: number;
}

/** SC-05, FR-06, IADR-0545（#1923 段 2）: 並べ替えの選択肢（BFF の `sort` と同じ語彙）。既定は更新日時の新しい順。 */
export type DocumentSort = BffDocumentListSort;
export const DEFAULT_DOCUMENT_SORT: DocumentSort = 'updated-desc';

/** 状態の絞り込み（BFF の `status` と同じ語彙）。`''` は「すべて」。 */
export type DocumentStatusFilter = BffDocumentListStatus | '';

/**
 * 一覧の 1 回の要求（ページ ＋ 並べ替え ＋ 絞り込み）。
 *
 * 表題の検索語（`q`）は前後の空白を除いて送り、空なら送らない（BFF も同じく空を無指定として扱う）。
 */
export interface DocumentListRequest extends DocumentPageRequest {
  sort: DocumentSort;
  q: string;
  status: DocumentStatusFilter;
}

/** 表題の検索語の上限（BFF の `q` の上限 200 文字と同じ）。入力欄の `maxLength` に使う。 */
export const DOCUMENT_TITLE_QUERY_MAX = 200;

/**
 * 要求をクエリ引数へ写す。**既定値は載せない**（既定の並べ替え・空の検索語・「すべて」）——
 * 同じ一覧が別のキャッシュキーに割れないように、また従前の URL（`?offset=&limit=`）を保つため。
 */
function toListParams(request: DocumentListRequest): BffDocumentListParams {
  const params: BffDocumentListParams = { offset: request.offset, limit: request.limit };
  if (request.sort !== DEFAULT_DOCUMENT_SORT) params.sort = request.sort;
  const q = request.q.trim();
  if (q !== '') params.q = q;
  if (request.status !== '') params.status = request.status;
  return params;
}

/** 表題・状態のどちらかで絞り込んでいるか（要約の文と空の表示を出し分ける）。 */
export function isFiltered(request: Pick<DocumentListRequest, 'q' | 'status'>): boolean {
  return request.q.trim() !== '' || request.status !== '';
}

/**
 * 応答封筒から 1 ページを取り出す。**`items` が配列でなければ空へ縮退させる**
 * （`okArray` と同じ理由。`bffFetch` は本文なしで `{}` を返す——IADR-0135 決定 7［2026-08-06 追記］）。
 */
function okPage(res: Parameters<typeof okData>[0]): DocumentListPageDto {
  const page = okData(res) as Partial<DocumentListPageDto>;
  const total = page.total ?? 0;
  return {
    items: Array.isArray(page.items) ? page.items : [],
    total,
    offset: page.offset ?? 0,
    limit: page.limit ?? DEFAULT_DOCUMENT_PAGE_SIZE,
    // ［2026-10-15 / #1923 段 2］項目を持たない応答（旧い BFF）は「絞り込みなし」として読む。
    unfilteredTotal: page.unfilteredTotal ?? total,
    statusCounts: page.statusCounts ?? {},
  };
}

/**
 * 一覧の 1 ページ（ABAC スコープ内のみ返る。`total` も BFF が権限で絞った後の件数）。
 * ［2026-10-15 / #1923 段 2・IADR-0545］並べ替え・表題の部分一致・状態の絞り込みも同じ要求で送る
 * （並べ替えは文書サービス、絞り込みは BFF が ABAC の後に当てる。`unfilteredTotal` / `statusCounts` は絞り込み前の数）。
 *
 * ページを替える間は前のページを出したままにする（`keepPreviousData`）——読み込み中の表示へ切り替えると、
 * ページ送りのたびに表が消えて位置を見失う。
 */
export function useAdminDocuments(request: DocumentListRequest) {
  const params = toListParams(request);
  return useBffDocumentList<DocumentListPageDto, unknown>(params, {
    query: {
      queryKey: getBffDocumentListQueryKey(params),
      select: okPage,
      placeholderData: keepPreviousData,
    },
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
