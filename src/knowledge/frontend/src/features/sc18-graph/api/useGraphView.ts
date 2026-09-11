import {
  getBffGraphEdgeTypesQueryKey,
  getBffGraphNeighborsQueryKey,
  useBffGraphEdgeTypes,
  useBffGraphNeighbors,
} from '@foundation/api/generated/graph/graph';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type {
  BffGraphNeighborsParams,
  EdgeTypeCatalogItem,
  GraphView,
} from '@foundation/api/generated/bff.schemas';
import type { GraphSearch } from '../routes/sc18GraphRoute';

// SC-18, UC-10, FR-17: グラフ読み取り（/bff/graph/*）。サーバー状態は TanStack Query（ADR-0031）。
//
// - 近傍探索は **URL の検索パラメータが単一情報源**（root / hops / by / types）。
//   キャッシュキーへ全部含める —— どれかを変えると別の問い合わせである。
// - 🔴 **辺の型フィルタはサーバ側で適用される**（planning#446 / 05_screens §SC-18）。
//   ここでは types をクエリパラメータへ写すだけで、クライアント側で辺を間引かない。
// - 辺の型辞書（描き分け・フィルタの選択肢）は改名に追随するため辞書側で解決する（ADR-0033 決定 9）。

function neighborsParams(search: GraphSearch): BffGraphNeighborsParams {
  return {
    hops: search.hops,
    by: search.by,
    // 省略＝絞らない。空の types を送らない（「空 = 全部落とす」との解釈揺れを作らない）。
    ...(search.types && search.types.length > 0 ? { types: search.types.join(',') } : {}),
  };
}

// 🔴 **取得系の `TError` は `unknown` ではなく `Error` で束ねる**（三部品 `QueryState` へ渡すため）。
// `QueryState` は `UseQueryResult<T>`（＝ `TError = Error`）を受ける。実際に投げられるのは
// `ApiError extends Error` なので、`Error` と書くほうが実態に合う（`unknown` は「何が飛ぶか
// 分からない」という主張であり、この経路では偽である）。更新系（mutation）は QueryState を
// 通らないので `unknown` のままでよい。
export function useGraphNeighbors(search: GraphSearch) {
  const params = neighborsParams(search);
  return useBffGraphNeighbors<GraphView, Error>(search.root, params, {
    query: {
      queryKey: getBffGraphNeighborsQueryKey(search.root, params),
      select: okData,
      // 起点未指定では照会しない（空状態の案内を出す。SC-18 主要素 8 とは別の「未指定」状態）。
      enabled: search.root !== '',
    },
  });
}

export function useEdgeTypeCatalog() {
  return useBffGraphEdgeTypes<EdgeTypeCatalogItem[], unknown>({
    query: { queryKey: getBffGraphEdgeTypesQueryKey(), select: okArray },
  });
}
