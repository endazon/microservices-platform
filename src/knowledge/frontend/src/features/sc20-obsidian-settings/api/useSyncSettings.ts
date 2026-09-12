import { useQueryClient } from '@tanstack/react-query';
import {
  getBffPrivateNoteListQueryKey,
  getBffSyncSettingsGetQueryKey,
  useBffSyncSettingsGet,
  useBffSyncSettingsUpdate,
} from '@foundation/api/generated/private-notes/private-notes';
import { okData } from '@foundation/api/orvalSelect';
import type { SyncSettingsDto } from '@foundation/api/generated/bff.schemas';

// SC-20 主要素 3, UC-11, FR-20, ADR-0037 決定 3・4: 同期対象フォルダの照会と置き換え。
// サーバー状態は TanStack Query（ADR-0031）。呼び出しは **orval 生成フック**（IADR-0135 決定 1）。
//
// 🔴 **置き換えは全量である。** 契約 `UpdateSyncSettingsRequest` は `targetFolders` の配列を
// まるごと受ける（1 件ずつ足し引きする口は無い）。**画面はいま見えている一覧を土台に
// 新しい配列を組み立てて送る** —— 差分の口を自作しない。
//
// 🔴 **対象から外しても資料は消えない**（ADR-0037 決定 4）。外す操作は同期の停止であり、
// 後段では当該資料の `syncState` が `excluded` になるだけである。**確認ダイアログの文言が
// この事実を明言する**（components/SyncTargetFoldersPanel.tsx）。
//
// 🔴 **正規化はサーバが持つ**（前後の `/` の除去・重複・上限 100 件・各 1024 文字は
// 契約が 400 で返す）。画面が同じ規則を実装し直すと、2 つの規則が静かにずれる。

// **export しない**（未使用 export の床を押し上げないため。SC-19 と同じ）。
const syncSettingsKey = getBffSyncSettingsGetQueryKey();

/** 本人の同期設定（対象フォルダの一覧）。 */
// 🔴 取得系の `TError` は `unknown` ではなく `Error` で束ねる（三部品 `QueryState` へ渡すため）。
export function useSyncSettings() {
  return useBffSyncSettingsGet<SyncSettingsDto, Error>({
    query: { queryKey: syncSettingsKey, select: okData },
  });
}

/**
 * 対象フォルダの置き換え。
 *
 * 成功後は `invalidateQueries` だけを行う（IADR-0127 決定 5）。**個人資料の一覧も無効化する** ——
 * 対象フォルダが変わると各資料の `syncState`（同期対象／対象外）が変わるため、
 * SC-19 の一覧を古いまま残すと「外したのに同期対象のまま」に見える。
 */
export function useSyncSettingsActions() {
  const queryClient = useQueryClient();
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: syncSettingsKey });
    void queryClient.invalidateQueries({ queryKey: getBffPrivateNoteListQueryKey() });
  };

  return { update: useBffSyncSettingsUpdate<unknown>({ mutation: { onSuccess: invalidate } }) };
}
