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
// 🔴 **当該文書での絞り込みはサーバ側で行う**（#1104。［2026-08-31］従前ここには
//   「画面側で行う」と書いてあったが、その記述は失効した）。
//   後段（`/bff/graph/suggestions`）が `documentId` を受け、**その文書を両端のいずれかに持つ提案
//   だけ**を返す。SC-21（棚卸しの一覧）は `documentId` を送らず、従来どおり権限内の全件を引く。
//   ⚠️ **クライアントで間引かない。** 間引くと表示件数と取得件数がずれ、
//   0 件の意味（「無い」／「絞られた」）が読めなくなる（SC-21 の `useAiSuggestions` と同じ作法）。
//   **秘匿の実施点は依然としてサーバ側の ABAC** である（権限外の提案は件数を含め届かない）——
//   `documentId` は転送量を減らす絞りであって、秘匿を担ってはいない。

// **export しない** —— feature の外から使う口を作ると未使用 export の床（check-knip）を押し上げる
// （SC-21 の `suggestionParams` と同じ理由）。
function pendingParams(documentId: string): BffGraphSuggestionsParams {
  return { state: 'pending', documentId };
}

/**
 * 当該文書に関わる `pending` の提案。
 *
 * `isPending` / `isError` は呼び出し側が欄の出し分けに使う（0 件と「引けない」を混同しない）。
 *
 * **クエリキーは文書ごとに分かれる**（パラメータに `documentId` が入るため）。
 * したがって承認・却下後の無効化も文書ごとになる —— `useSuggestionActions` に同じ ID を渡す。
 */
export function useDocumentSuggestions(documentId: string) {
  const params = useMemo(() => pendingParams(documentId), [documentId]);
  const query = useBffGraphSuggestions<AiSuggestion[], unknown>(params, {
    query: { queryKey: getBffGraphSuggestionsQueryKey(params), select: okArray },
  });

  return { items: query.data ?? [], isPending: query.isPending, isError: query.isError };
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
 *
 * 🔴 **`documentId` を受け取る**（#1104）。絞りをサーバへ移したことでクエリキーが文書ごとに
 * 分かれたため、**引数を落とすと「承認したのに欄から消えない」** ——
 * 無効化が誰も見ていないキーへ飛ぶからである。
 */
export function useSuggestionActions(documentId: string) {
  const queryClient = useQueryClient();
  const suggestionsKey = getBffGraphSuggestionsQueryKey(pendingParams(documentId));
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: suggestionsKey });
  const onChanged = { mutation: { onSuccess: invalidate } };

  return {
    approve: useBffGraphSuggestionApprove<unknown>(onChanged),
    reject: useBffGraphSuggestionReject<unknown>(onChanged),
  };
}
