import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-02, UC-01, FR-03/FR-05: 横断検索（POST /bff/search）。
// IADR-0126 決定 3・4: 検索語は URL（?q=）が単一情報源であり、本フックはそれを受け取るだけである。
// キャッシュキーを検索語にすることで、戻る操作・同じ語での再訪が再要求にならない。

/** 検索結果 1 件（BFF の `SearchResultDto` に対応）。 */
export interface SearchResult {
  chunkId: string;
  documentId: string;
  documentTitle: string;
  text: string;
  score: number;
  markdownUri?: string | null;
  attributes?: Record<string, string>;
  tags?: string[];
}

export interface SearchResponse {
  results: SearchResult[];
  totalHits: number;
  elapsedMs: number;
}

/** 1 ページあたりの取得件数（BFF 側の上限は 50）。ページングは計画が送り方を定めていない（画面仕様書 §未決事項 2）。 */
export const SEARCH_TOP_K = 20;

const EMPTY: SearchResponse = { results: [], totalHits: 0, elapsedMs: 0 };

/**
 * 検索語で `/bff/search` を引く。空文字のときは**要求を出さない**。
 *
 * FR-05: クライアントは ABAC スコープを送らない。権限解決はサーバ側（BFF が JWT から解決）で行われ、
 * 権限外の文書は結果に現れない（deny-by-default → 空一覧。存在秘匿・IADR-0009）。
 */
export function useSearchQuery(query: string) {
  const q = query.trim();
  return useQuery({
    queryKey: ['bff', 'search', q],
    queryFn: async () =>
      (await apiFetch<SearchResponse>('/search', {
        method: 'POST',
        json: { query: q, topK: SEARCH_TOP_K },
      })) ?? EMPTY,
    enabled: q.length > 0,
  });
}
