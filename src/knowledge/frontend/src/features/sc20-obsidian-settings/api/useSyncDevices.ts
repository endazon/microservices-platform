import { useQueryClient } from '@tanstack/react-query';
import {
  getBffSyncDeviceListQueryKey,
  useBffSyncDeviceIssue,
  useBffSyncDeviceList,
  useBffSyncDeviceReissue,
  useBffSyncDeviceRevoke,
  useBffSyncDeviceRevokeAll,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray } from '@foundation/api/orvalSelect';
import type { SyncDeviceDto } from '@foundation/api/generated/bff.schemas';

// SC-20, UC-11, FR-20: 同期端末（Obsidian プラグインの接続先）の照会と、トークンの発行・再発行・失効。
// サーバー状態は TanStack Query（ADR-0031）。呼び出しは **orval 生成フック**（IADR-0135 決定 1）。
//
// 🔴 **画面は同期トークンで API を呼ばない。** 同期トークンは Obsidian プラグインが使う
// **別系統の資格情報**であり（ADR-0037 課題 2）、ブラウザは BFF セッションで話す。
// **平文のトークンが載るのは発行・再発行の応答だけ**で、一覧にも他のどの応答にも現れない。
//
// 🔴 **自動更新（リフレッシュ）の口は作らない**（ADR-0037 決定 15）。有効期限 30 日は
// 「失効操作を忘れた場合の最終的な歯止め」であり、自動更新はその統制を実質的に無効化する。
// 更新は**手動再発行だけ**である。

// **export しない**（未使用 export の床を押し上げないため。SC-19 と同じ）。
const syncDevicesKey = getBffSyncDeviceListQueryKey();

/** 端末一覧（本人のもののみ）。 */
// 🔴 **取得系の `TError` は `unknown` ではなく `Error` で束ねる**（三部品 `QueryState` へ渡すため）。
// `QueryState` は `UseQueryResult<T>`（＝ `TError = Error`）を受ける。実際に投げられるのは
// `ApiError extends Error` なので、`Error` と書くほうが実態に合う（`unknown` は「何が飛ぶか
// 分からない」という主張であり、この経路では偽である）。更新系（mutation）は QueryState を
// 通らないので `unknown` のままでよい。
export function useSyncDevices() {
  return useBffSyncDeviceList<SyncDeviceDto[], Error>({
    query: { queryKey: syncDevicesKey, select: okArray },
  });
}

/**
 * 発行・再発行・個別失効・一括失効。
 *
 * 成功後は `invalidateQueries` だけを行う（IADR-0127 決定 5）。
 * 列挙を手書きの配列で持たないのは SC-19 と同じ理由である（口を足したときに読まれない失敗を作らない）。
 */
export function useSyncDeviceActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: syncDevicesKey });
  const onChanged = { mutation: { onSuccess: invalidate } };

  return {
    issue: useBffSyncDeviceIssue<unknown>(onChanged),
    reissue: useBffSyncDeviceReissue<unknown>(onChanged),
    revoke: useBffSyncDeviceRevoke<unknown>(onChanged),
    revokeAll: useBffSyncDeviceRevokeAll<unknown>(onChanged),
  };
}
