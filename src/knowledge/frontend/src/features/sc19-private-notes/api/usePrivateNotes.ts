import { useQueryClient } from '@tanstack/react-query';
import {
  getBffPrivateNoteListQueryKey,
  useBffPrivateNoteCreate,
  useBffPrivateNoteExposure,
  useBffPrivateNoteList,
  useBffPrivateNotePurge,
  useBffPrivateNoteRestore,
  useBffPrivateNoteSoftDelete,
} from '@foundation/api/generated/private-notes/private-notes';
import { okData } from '@foundation/api/orvalSelect';
import type { PrivateNoteListResponse } from '@foundation/api/generated/bff.schemas';

// SC-19, UC-11, FR-19: 個人資料の照会・作成・論理削除・復元・完全削除・露出設定。
// サーバー状態は TanStack Query（ADR-0031）。呼び出しは **orval 生成フック**（IADR-0135 決定 1）。
//
// 🔴 **一覧の問い合わせは 1 本だけである。** 後段は削除済みも同じ応答に載せ、容量もそこに付ける
// （契約 `PrivateNoteListResponse`）。タブごとに問い合わせを分けると、
// **「うち削除済み」の内訳と件数バッジの数え方が 2 つになる** —— 契約側が明示的に避けた形である。
//
// 🔴 **本人性の判定を画面が持たない。** 誰の資料かは主体（セッション）で決まり、要求側は指定できない
// （`PrivateNoteDto` は所有者を運ぶ項目を持たない）。**画面から `ownerId` 相当を送る口を作らない。**
//
// IADR-0127 決定 5: 更新系の成功後は `invalidateQueries` だけを行う（手書きの再取得を持たない）。
// 無効化の対象は**一覧 1 本**でよい —— 容量も削除済みも同じ応答に載っているためである。

// **export しない** —— feature の外から使う口を作ると未使用 export の床（check-knip）を押し上げる
// （SC-21 の `suggestionParams` と同じ理由）。
const privateNotesKey = getBffPrivateNoteListQueryKey();

/**
 * 一覧＋容量（本人のもののみ）。
 *
 * `okArray` ではなく `okData` を使う —— 応答は配列ではなく `{ usage, notes }` の封筒である。
 */
export function usePrivateNotes() {
  return useBffPrivateNoteList<PrivateNoteListResponse, unknown>({
    query: { queryKey: privateNotesKey, select: okData },
  });
}

/**
 * 書き込みの束（作成・論理削除・復元・完全削除・露出）。
 *
 * **列挙を手書きの配列で持たない。** 画面は戻り値のオブジェクトから `Object.values` で
 * 「直近の操作の結果」を導く（IADR-0127 決定 7 と同じ作法）——
 * 手書きの配列にすると、口を足したときに「読まれない失敗」が静かに生まれる。
 */
export function usePrivateNoteActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: privateNotesKey });
  const onChanged = { mutation: { onSuccess: invalidate } };

  return {
    create: useBffPrivateNoteCreate<unknown>(onChanged),
    softDelete: useBffPrivateNoteSoftDelete<unknown>(onChanged),
    restore: useBffPrivateNoteRestore<unknown>(onChanged),
    purge: useBffPrivateNotePurge<unknown>(onChanged),
    exposure: useBffPrivateNoteExposure<unknown>(onChanged),
  };
}
