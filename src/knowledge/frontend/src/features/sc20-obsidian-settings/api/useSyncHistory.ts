import {
  getBffSyncHistoryListQueryKey,
  useBffSyncHistoryList,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray } from '@foundation/api/orvalSelect';
import type { SyncHistoryEntryDto } from '@foundation/api/generated/bff.schemas';

// SC-20 主要素 6, UC-11, FR-20, ADR-0099 / IADR-0446: 同期履歴の照会。
//
// 🔴 **読み口は 1 本・照会だけである。** 履歴は監査ログの投影であり（ADR-0099 決定 1）、
// **画面から消す・編集する口は契約に無い**。よって更新系のミューテーションを持たない。
//
// 🔴 **件数は画面が決めない。** 契約 `GET /bff/private-notes/sync-history` が「端末横断で
// 新しい順に直近 50 件」を返す（ADR-0099 決定 4。BFF が `limit=50` を固定して後段へ渡す）。
// **画面側に件数の引数も「さらに読み込む」の導線も置かない** —— 画面が独自に件数を決めると、
// 「画面に出ている履歴の長さ」と「実際に保持している長さ（3 年。決定 3）」が食い違って読める。
//
// 🔴 **取得系の `TError` は `Error` で束ねる**（`QueryState` へ渡すため。`useSyncConflicts` と同じ）。

// **export しない** —— feature の外から使う口を作ると未使用 export の床（check-knip）を押し上げる。
const syncHistoryKey = getBffSyncHistoryListQueryKey();

/** 同期履歴（本人の記録のみ・新しい順・直近 50 件）。 */
export function useSyncHistory() {
  return useBffSyncHistoryList<SyncHistoryEntryDto[], Error>({
    query: { queryKey: syncHistoryKey, select: okArray },
  });
}
