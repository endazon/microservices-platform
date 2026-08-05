import { useQuery } from '@tanstack/react-query';
import { bffSearch } from '@foundation/api/generated/search/search';
import { okData } from '@foundation/api/orvalSelect';

// SC-02, UC-01, FR-03/FR-05: 横断検索（POST /bff/search）。
// IADR-0126 決定 3・4: 検索語は URL（?q=）が単一情報源であり、本フックはそれを受け取るだけである。
// キャッシュキーを検索語にすることで、戻る操作・同じ語での再訪が再要求にならない。
//
// IADR-0135 決定 2（#519）: `/bff/search` は **POST** なので orval が生成するのは `useMutation`
// （`useBffSearch`）であり、**照会としては使えない**——キャッシュに載らず、上の性質が失われる。
// そこで**生成された操作関数 `bffSearch` を `useQuery` の `queryFn` に据える**。
// 型（`SearchRequest` / `SearchResponse`）も URL も生成物由来であり、出口も `bffFetch` →
// `apiRequest` の一本道である（手書き HTTP クライアントではない。IADR-0121 決定 3）。

/** 1 ページあたりの取得件数（BFF 側の上限は 50）。ページングは計画が送り方を定めていない（画面仕様書 §未決事項 2）。 */
export const SEARCH_TOP_K = 20;

/** キャッシュキー。**検索語を含める**——生成フックが無い面なので、載せ替え前のキーをそのまま使う。 */
export const searchQueryKey = (query: string) => ['bff', 'search', query] as const;

/**
 * 検索語で `/bff/search` を引く。空文字のときは**要求を出さない**。
 *
 * FR-05: クライアントは ABAC スコープを送らない。権限解決はサーバ側（BFF が JWT から解決）で行われ、
 * 権限外の文書は結果に現れない（deny-by-default → 空一覧。存在秘匿・IADR-0009）。
 */
export function useSearchQuery(query: string) {
  const q = query.trim();
  return useQuery({
    queryKey: searchQueryKey(q),
    queryFn: ({ signal }) => bffSearch({ query: q, topK: SEARCH_TOP_K }, { signal }),
    select: okData,
    enabled: q.length > 0,
  });
}
