import {
  getBffGraphEdgeTypesQueryKey,
  getBffGraphSuggestionsQueryKey,
  useBffGraphEdgeTypes,
  useBffGraphSuggestions,
} from '@foundation/api/generated/graph/graph';
import { okArray } from '@foundation/api/orvalSelect';
import type {
  AiSuggestion,
  BffGraphSuggestionsParams,
  EdgeTypeCatalogItem,
} from '@foundation/api/generated/bff.schemas';
import type { AiSuggestionSearch } from '../routes/sc21AiSuggestionsRoute';

// SC-21, UC-10, FR-18: AI 提案の読み取り（/bff/graph/suggestions）。
// サーバー状態は TanStack Query（ADR-0031）。
//
// 🔴 **読み取りしか無い。** 承認・却下の mutation を本 feature へ置かない ——
// 本画面は書き込みを一切しない（05_screens §SC-21）。承認は SC-03 の承認欄が担う。
//
// - **URL の検索パラメータが単一情報源**（state / kind）。キャッシュキーへ全部含める。
// - 🔴 **絞りはサーバ側で適用される。** ここでは URL をクエリパラメータへ写すだけで、
//   取得後にクライアントで間引かない —— 間引くと「権限内の全件」と「表示中の件数」がずれ、
//   利用者は棚卸しの残量を読み違える。
// - **`all` は送る値である**（後段が「絞りの解除」として解釈する）。一方 `kind` の `all` は
//   **送らない**（後段の `kind` は「未指定＝絞らない」であり、`all` という値を持たない）。

// URL の検索パラメータ → BFF のクエリパラメータ。**片方向の写像だけを行う。**
// **export しない** —— 外から使う口を作ると未使用 export の床（check-knip）を押し上げる。
function suggestionParams(search: AiSuggestionSearch): BffGraphSuggestionsParams {
  return {
    state: search.state,
    ...(search.kind === 'all' ? {} : { kind: search.kind }),
  };
}

export function useAiSuggestions(search: AiSuggestionSearch) {
  const params = suggestionParams(search);
  return useBffGraphSuggestions<AiSuggestion[], unknown>(params, {
    query: { queryKey: getBffGraphSuggestionsQueryKey(params), select: okArray },
  });
}

/**
 * 辺の型カタログ（表示名の解決に使う）。
 *
 * 🔴 **表示名は辞書側で解決する**（ADR-0033 決定 9）——型を改名しても一覧が追随するため。
 * 提案の DTO は `edgeTypeId` しか持たない。
 *
 * **SC-18 が持つ同名のフックを再利用しない。** feature の外から `api/` を直接 import しないのが
 * 本リポジトリの feature 境界（IADR-0262 決定 4）であり、共有したいなら `foundation` へ上げる
 * ことになる。**辺の型辞書は画面の関心であって基盤の関心ではない**ので、5 行の重複を選ぶ。
 */
export function useEdgeTypeCatalog() {
  return useBffGraphEdgeTypes<EdgeTypeCatalogItem[], unknown>({
    query: { queryKey: getBffGraphEdgeTypesQueryKey(), select: okArray },
  });
}
