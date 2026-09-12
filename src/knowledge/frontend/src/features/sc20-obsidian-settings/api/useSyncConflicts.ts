import { useQueryClient } from '@tanstack/react-query';
import {
  getBffPrivateNoteListQueryKey,
  getBffSyncConflictGetQueryKey,
  getBffSyncConflictListQueryKey,
  useBffSyncConflictGet,
  useBffSyncConflictList,
  useBffSyncConflictResolve,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray, okData } from '@foundation/api/orvalSelect';
import type {
  SyncConflictDetailDto,
  SyncConflictSummaryDto,
} from '@foundation/api/generated/bff.schemas';

// SC-20 主要素 5, UC-11, FR-20, ADR-0037 決定 7 / IADR-0352: 同期の競合の照会と解決。
//
// 🔴 **自動解決の口は存在しない。** 契約 `ResolveSyncConflictRequest` の値は
// `local` / `server` / `both` の 3 つだけで、「後勝ち」に相当する値が無い（ADR-0037 決定 7）。
// **画面にも自動解決の選択肢を置かない** —— 置けるように見せると、利用者は
// 「面倒だから自動で」を選び、**本文が黙って失われる**。
//
// 🔴 **本文は詳細だけが返す。** 一覧（`SyncConflictSummaryDto`）はタイトル・パス・検出日時・
// 端末・版しか持たない。2 ペイン差分の材料（`localContent` / `serverContent`）は
// **選んだ 1 件を引いたときだけ**取りに行く（一覧を重くしない）。

// **export しない**（未使用 export の床を押し上げないため）。
const conflictsKey = getBffSyncConflictListQueryKey();

/** 未解決の競合の一覧（検出日時の新しい順で後段が返す）。 */
export function useSyncConflicts() {
  return useBffSyncConflictList<SyncConflictSummaryDto[], Error>({
    query: { queryKey: conflictsKey, select: okArray },
  });
}

/**
 * 競合 1 件の詳細（両版の本文）。
 *
 * **呼び出し側は「選んでいるときだけ」この hook を持つ部品を描く。** 生成フックの `enabled` は
 * `id` が null / undefined でないことしか見ないため、空文字を渡すと `/conflicts/` を叩いてしまう。
 */
export function useSyncConflictDetail(id: string) {
  return useBffSyncConflictGet<SyncConflictDetailDto, Error>(id, {
    query: { queryKey: getBffSyncConflictGetQueryKey(id), select: okData },
  });
}

/**
 * 競合の解決（3 択）。
 *
 * 成功後は `invalidateQueries` だけを行う（IADR-0127 決定 5）。**個人資料の一覧も無効化する** ——
 * 競合が解けると当該資料の `syncState` が `conflict` でなくなり、`both` では資料が 1 件増える。
 */
export function useSyncConflictActions() {
  const queryClient = useQueryClient();
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: conflictsKey });
    void queryClient.invalidateQueries({ queryKey: getBffPrivateNoteListQueryKey() });
  };

  return { resolve: useBffSyncConflictResolve<unknown>({ mutation: { onSuccess: invalidate } }) };
}
