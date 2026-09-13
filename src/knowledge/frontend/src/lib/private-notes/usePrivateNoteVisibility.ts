import {
  getBffPrivateNoteListQueryKey,
  useBffPrivateNoteList,
} from '@foundation/api/generated/private-notes/private-notes';
import { okData } from '@foundation/api/orvalSelect';
import type { PrivateNoteDto } from '@foundation/api/generated/bff.schemas';

// FR-19, UC-11, SC-03, SC-19, 計画 ADR-0102 決定 1・2 / [[IADR-0451]] (#1455):
// **個人資料 1 件の公開範囲（3 状態）を、所有者だけが読める口から引く。**
//
// 🔴 **共有先の写し（`DocumentDto.sharedWith`）からは導かない**（計画 ADR-0102 決定 2）——
// 写しは指定先の種別を運ばないため、**個人指定とグループ指定を区別できない**。3 状態を持っているのは
// 一覧の口（`GET /bff/private-notes`）だけであり、**その値はサーバが導出する**（画面で導出し直さない。
// SC-19 と導出点を 2 つにしない）。
//
// 🔴 **口は本人の資料しか返さない。** よって所有者以外には当該 ID が見つからず、**公開範囲は描けない**
// （計画 ADR-0102 決定 1）。**可視性を決めるのはサーバであり、画面の分岐ではない。**
//
// **query key は生成物の factory をそのまま使う** —— SC-19 の一覧と同じキーになるので、
// 2 画面で 1 つのキャッシュを共有する（同じ応答を 2 度取りに行かない）。

/**
 * 文書 ID に対応する個人資料（本人のものだけ）。所有者でなければ `undefined` を返す。
 *
 * `enabled` が偽のときは問い合わせない（呼び出し側が「個人資料か」「自分の資料か」で門を置く）。
 * **門は無駄な取得を避けるためのものであり、可視性の統制ではない**（統制は後段が持つ）。
 */
export function usePrivateNoteVisibility(documentId: string, enabled: boolean) {
  return useBffPrivateNoteList<PrivateNoteDto | undefined, Error>({
    query: {
      queryKey: getBffPrivateNoteListQueryKey(),
      // `PrivateNoteDto.id` は**文書 ID** である（後段 `PrivateNoteMapper` が `PrivateNote.DocumentId` を写す）。
      select: (res) => okData(res).notes.find((n) => n.id === documentId),
      enabled,
    },
  });
}
