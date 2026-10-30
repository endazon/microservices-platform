import { useQueryClient } from '@tanstack/react-query';
import {
  getBffGraphEdgeTypesQueryKey,
  getBffGraphLinkCandidatesQueryKey,
  getBffGraphNeighborsQueryKey,
  useBffGraphEdgeCreate,
  useBffGraphEdgeTypes,
  useBffGraphLinkCandidates,
} from '@foundation/api/generated/graph/graph';
import { okArray } from '@foundation/api/orvalSelect';
import type {
  EdgeTypeCatalogItem,
  GraphLinkCandidateDto,
} from '@foundation/api/generated/bff.schemas';
import { useLookupTerm } from './lookupTerm';

// FR-17, SC-19, ADR-0034 決定 2・8, [[IADR-0655]] (#2343): リンク作成（関連文書の明示的な付与）の読み書き。
//
// - **候補は後段が作成者の read スコープで絞る**（辺の作成と同じ述語。個人資料は自分のものだけ）。画面は絞らない。
// - 検索語の下限とデバウンスは `lookupTerm.ts` の 1 か所に寄せる（共有先の検索と同じ作法）。
// - 辺の型の辞書は SC-18・SC-21 にも同名のフックがあるが、feature の外の `api/` は import しない（IADR-0262 決定 4）。

/** リンク先の補完候補（表題の部分一致・2 文字以上）。件数は返らない。 */
export function useLinkCandidates(rawQuery: string) {
  const { term, enabled } = useLookupTerm(rawQuery);
  return {
    enabled,
    query: useBffGraphLinkCandidates<GraphLinkCandidateDto[], Error>(
      { q: term },
      {
        query: {
          queryKey: getBffGraphLinkCandidatesQueryKey({ q: term }),
          select: okArray,
          enabled,
        },
      },
    ),
  };
}

/** 辺の型（`/bff/graph/edge-types`。認証のみ・使用件数なし）。 */
export function useLinkEdgeTypes() {
  return useBffGraphEdgeTypes<EdgeTypeCatalogItem[], unknown>({
    query: { queryKey: getBffGraphEdgeTypesQueryKey(), select: okArray },
  });
}

/**
 * 辺の作成。成功したら両端の近傍（SC-18 が読む照会）を引き直させる —— 作った辺が SC-18 のグラフに出る。
 */
export function useCreateLink() {
  const queryClient = useQueryClient();
  return useBffGraphEdgeCreate<unknown>({
    mutation: {
      onSuccess: (_data, { data }) => {
        for (const id of [data.sourceDocumentId, data.targetDocumentId]) {
          void queryClient.invalidateQueries({ queryKey: getBffGraphNeighborsQueryKey(id) });
        }
      },
    },
  });
}
