import { useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import {
  getBffGraphEdgeTypesQueryKey,
  getBffGraphSuggestionsQueryKey,
  useBffGraphEdgeTypes,
  useBffGraphSuggestionApprove,
  useBffGraphSuggestionReject,
  useBffGraphSuggestions,
} from '@foundation/api/generated/graph/graph';
import { okArray } from '@foundation/api/orvalSelect';
import type {
  AiSuggestion,
  BffGraphSuggestionsParams,
  EdgeTypeCatalogItem,
} from '@foundation/api/generated/bff.schemas';

// SC-03, UC-10, FR-18: 文書詳細の AI 提案承認欄が使うサーバー状態（#450）。
// サーバー状態は TanStack Query（ADR-0031）、呼び出しは orval 生成フック（IADR-0135 決定 1）。
//
// ■ 05_screens §SC-03「AI 提案の承認欄」: **本欄に既定で表示するのは `pending` の提案**である。
//   したがって問い合わせは `state=pending` 固定で、画面に状態フィルタを持たない
//   （状態を跨いだ棚卸しは SC-21 の仕事である）。
//
// 🔴 **当該文書での絞り込みは画面側で行う。**
//   後段（`/bff/graph/suggestions`）は `state` と `kind` しか受けず、**文書での絞り込みを持たない。**
//   SC-21（一覧）はそれで足りるが、SC-03 は「当該文書を両端のいずれかとする提案」だけを描く。
//   本来はサーバ側で絞るべきで、そのほうが往復も転送量も小さい —— **後段の変更を伴うため
//   本作業（#450）の射程外**とし、追随 issue へ回した（[[IADR-0300]] 決定 6・作業仕様書 §未決事項 1）。
//   ⚠️ **したがってここでの間引きは「権限内の全件」を減らしていない。** 権限による絞りは
//   サーバ側が済ませており（権限外の提案は件数を含め届かない）、ここは表示対象の限定である。

// **export しない** —— feature の外から使う口を作ると未使用 export の床（check-knip）を押し上げる
// （SC-21 の `suggestionParams` と同じ理由）。
const PENDING_PARAMS: BffGraphSuggestionsParams = { state: 'pending' };
const suggestionsKey = getBffGraphSuggestionsQueryKey(PENDING_PARAMS);

/** 当該文書を端点に持つ提案か（リンク提案は両端、タグ提案は対象文書 1 件を見る）。 */
function touches(suggestion: AiSuggestion, documentId: string): boolean {
  return suggestion.sourceDocumentId === documentId || suggestion.targetDocumentId === documentId;
}

/**
 * 当該文書に関わる `pending` の提案。
 *
 * **返す形はクエリの結果そのままではなく、絞り込み後の配列を添えたものである。**
 * `isPending` / `isError` は呼び出し側が欄の出し分けに使う（0 件と「引けない」を混同しない）。
 */
export function useDocumentSuggestions(documentId: string) {
  const query = useBffGraphSuggestions<AiSuggestion[], unknown>(PENDING_PARAMS, {
    query: { queryKey: suggestionsKey, select: okArray },
  });

  const items = useMemo(
    () => (query.data ?? []).filter((s) => touches(s, documentId)),
    [query.data, documentId],
  );

  return { items, isPending: query.isPending, isError: query.isError };
}

/**
 * 辺の型カタログ（表示名の解決に使う）。
 *
 * 🔴 **表示名は辞書側で解決する**（ADR-0033 決定 9）—— 型を改名しても追随するためである。
 * 提案の DTO は `edgeTypeId` しか持たない。
 *
 * **SC-21 が持つ同名のフックを再利用しない。** feature の外から `api/` を直接 import しないのが
 * 本リポジトリの feature 境界（IADR-0262 決定 4）であり、共有したいなら `foundation` へ上げる
 * ことになる。**辺の型辞書は画面の関心であって基盤の関心ではない**ので、数行の重複を選ぶ。
 */
export function useEdgeTypeNames() {
  const catalog = useBffGraphEdgeTypes<EdgeTypeCatalogItem[], unknown>({
    query: { queryKey: getBffGraphEdgeTypesQueryKey(), select: okArray },
  });

  return useMemo(() => {
    const names = new Map<string, string>();
    for (const type of catalog.data ?? []) names.set(type.id, type.name);
    return names;
  }, [catalog.data]);
}

/**
 * 承認・却下（1 件ずつ）。
 *
 * 🔴 **一括の口を作らない**（FR-18・05_screens §SC-21「描いてはいけないもの」）。
 * 引数に配列を取る形にした時点で、画面側は一括ボタンを置けてしまう。
 *
 * IADR-0127 決定 5: 更新系の成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない）。
 * 無効化の対象は `pending` の一覧 1 本でよい —— 承認・却下はいずれも `pending` から出す遷移であり、
 * 欄から消えることが利用者にとっての結果である。
 */
export function useSuggestionActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: suggestionsKey });
  const onChanged = { mutation: { onSuccess: invalidate } };

  return {
    approve: useBffGraphSuggestionApprove<unknown>(onChanged),
    reject: useBffGraphSuggestionReject<unknown>(onChanged),
  };
}
