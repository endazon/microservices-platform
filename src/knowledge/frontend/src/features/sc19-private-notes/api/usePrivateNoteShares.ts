import { useQueryClient } from '@tanstack/react-query';
import {
  getBffPrivateNoteListQueryKey,
  getBffPrivateNoteShareListQueryKey,
  useBffPrivateNoteShareGrant,
  useBffPrivateNoteShareList,
  useBffPrivateNoteShareRevoke,
} from '@foundation/api/generated/private-notes/private-notes';
import { okArray } from '@foundation/api/orvalSelect';
import type { DocumentShareDto } from '@foundation/api/generated/bff.schemas';

// SC-19 主要素 3, UC-11, FR-19, ADR-0036 D-06 / ADR-0098 / IADR-0253 段 4 / IADR-0445:
// 公開範囲の指定先（共有台帳）の照会・付与・取り消し。
//
// 🔴 **画面が送る `subjectType` は `user` だけである**（ADR-0098 決定 2）。契約は `group` も
// 受け付けたままだが（受け付けをやめると配線が入ったとき再び開く作業が生じる）、
// **認可がグループ共有を一度も評価しないため、設定できても閲覧できる主体は 1 人も居ない。**
// よって導線を置かない。型の上でも `group` を送る経路をここに作らない。
//
// 🔴 **開いているときだけ引く。** 一覧（`PrivateNotesPage`）の行数だけ問い合わせが飛ぶのを避ける
// ため、`enabled` をダイアログの開閉で制御する（生成フックの既定の `enabled` は id の有無しか見ない）。
//
// 🔴 **取得系の `TError` は `Error`・更新系は `unknown`**（`usePrivateNotes.ts` と同じ作法。
// 前者は `QueryState` へ渡すため、後者は `QueryState` を通らないため）。
//
// IADR-0127 決定 5: 更新系の成功後は `invalidateQueries` だけを行う。
// **無効化は shares と一覧の 2 本である** —— 付与・取り消しで `PrivateNoteDto.visibility` の
// 3 状態と `sharedUserCount` が変わるため、**一覧を無効化しないとバッジが古いまま残る。**

/**
 * 1 件の資料の指定先の一覧（付与順）。
 *
 * @param noteId 対象の資料
 * @param open ダイアログが開いているか。**閉じている間は問い合わせない。**
 */
export function usePrivateNoteShares(noteId: string, open: boolean) {
  return useBffPrivateNoteShareList<DocumentShareDto[], Error>(noteId, {
    query: {
      queryKey: getBffPrivateNoteShareListQueryKey(noteId),
      select: okArray,
      enabled: open,
    },
  });
}

/**
 * 指定先の付与と取り消し。
 *
 * **列挙を手書きの配列で持たない**（`usePrivateNoteActions` と同じ作法）。画面は戻り値の
 * オブジェクトから `Object.values` で「直近の操作の結果」を導く。
 */
export function usePrivateNoteShareActions(noteId: string) {
  const queryClient = useQueryClient();
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: getBffPrivateNoteShareListQueryKey(noteId) });
    void queryClient.invalidateQueries({ queryKey: getBffPrivateNoteListQueryKey() });
  };
  const onChanged = { mutation: { onSuccess: invalidate } };

  return {
    grant: useBffPrivateNoteShareGrant<unknown>(onChanged),
    revoke: useBffPrivateNoteShareRevoke<unknown>(onChanged),
  };
}
