import {
  getBffWikiPageByDocumentQueryKey,
  getBffWikiPageBySlugQueryKey,
  getBffWikiPageListQueryKey,
  getBffWikiSearchQueryKey,
  useBffWikiPageByDocument,
  useBffWikiPageBySlug,
  useBffWikiPageList,
  useBffWikiSearch,
} from '@foundation/api/generated/wiki/wiki';
import { okArray, okData } from '@foundation/api/orvalSelect';
import { ApiError } from '@foundation/api/ApiError';
import type {
  WikiPageSummary,
  WikiPageView,
  WikiSearchHit,
} from '@foundation/api/generated/bff.schemas';
import type { WikiSearch } from '../types/wikiSearch';

// SC-04, UC-07, FR-13, ADR-0073 決定 2・4 / IADR-0355 / IADR-0367 (#1200): Wiki 閲覧のデータ取得（`/bff/wiki/*`）。
//
// 4 本とも **orval 生成フック**で呼ぶ（IADR-0135 決定 1）。キーは生成キー（`['/bff/wiki/pages']` 等）。
// 後段の意味論は BFF が**作り替えずに透過**する（IADR-0355 決定 5）ので、画面が読むのはそのままの形である:
//   - 一覧・検索の **200 ＋ 空** ＝ 権限内に何も無い（deny-by-default）。「権限が無い」とは言わない。
//   - 個別取得の **404** ＝ 権限外・不存在・アーカイブ済みを区別しない（存在秘匿。IADR-0009）。中立に表示する。
//   - **502** ＝ Wiki.js に到達できない。**空で隠さない**（「壊れている」は「無い」と別の軸）。
//   - 401 は `apiClient` の再ログイン導線（`setUnauthorizedHandler`）が受ける。ここでは何もしない。

/** 404 は「不在」と「権限による秘匿」を区別しない（IADR-0009）。画面はどちらも中立に表示する。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.kind === 'notFound';
}

/** ページツリー。台帳は平坦（`wikiPath` = `doc/<id>`）で、後段が題名順に返す（IADR-0367 決定 2）。 */
export function useWikiPages() {
  return useBffWikiPageList<WikiPageSummary[], unknown>({
    query: { queryKey: getBffWikiPageListQueryKey(), select: okArray },
  });
}

/**
 * 本文。`page`（スラッグ）があればそれを、無ければ `doc`（文書 ID）を使う。
 * どちらも無ければ何も取りに行かない（`enabled: false` のまま）。
 *
 * **フックは常に 2 本とも呼ぶ**（条件で呼び分けると React のフック規則を破る）。
 */
export function useWikiPage(search: Pick<WikiSearch, 'page' | 'doc'>) {
  const slug = search.page ?? '';
  const doc = slug === '' ? (search.doc ?? '') : '';
  const bySlug = useBffWikiPageBySlug<WikiPageView, unknown>(slug, {
    query: { queryKey: getBffWikiPageBySlugQueryKey(slug), select: okData, enabled: slug !== '' },
  });
  const byDoc = useBffWikiPageByDocument<WikiPageView, unknown>(doc, {
    query: { queryKey: getBffWikiPageByDocumentQueryKey(doc), select: okData, enabled: doc !== '' },
  });
  return slug !== '' ? bySlug : byDoc;
}

/**
 * 検索。空白だけの語では取りに行かない（後段も 200 ＋ 空を返すが、往復そのものが無駄である）。
 * `limit` は載せない —— 既定 20 / 上限 50 は後段が唯一の情報源（IADR-0355 決定 5）。
 */
export function useWikiSearchHits(q: string | undefined) {
  const trimmed = q?.trim() ?? '';
  const params = trimmed === '' ? undefined : { q: trimmed };
  return useBffWikiSearch<WikiSearchHit[], unknown>(params, {
    query: { queryKey: getBffWikiSearchQueryKey(params), select: okArray, enabled: trimmed !== '' },
  });
}
