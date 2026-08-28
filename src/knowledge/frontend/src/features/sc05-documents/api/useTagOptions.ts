import { getBffTagListQueryKey, useBffTagList } from '@foundation/api/generated/tags/tags';
import { okData } from '@foundation/api/orvalSelect';
import type { TagDictionaryResponse } from '@foundation/api/generated/bff.schemas';

// FR-06, FR-09, UC-03, SC-05（#449）: 文書に付けられるタグの**値集合**を辞書から引く。
//
// 計画 05_screens §SC-05 の入力表は タグ =「**既定タグ辞書に整合**（**辞書は管理系ロールが
// 引ける照会口から取得する**）」であり、2026-08-05 の裁定（質問票 第12回 Q18）が理由まで書いている
// ——「**自由入力を許すと辞書に無いタグが増え、SC-09 で確定した規則〔参照が 1 件でもあるタグは
// 削除拒否・改名は既存文書へ追随・削除前に使用件数を示す〕が成り立たなくなる**」。
// 当該規則は「タグが辞書の識別子を参照している」ことを前提にしている。
//
// **口は SC-09 と同じ `/bff/tags`（読み取りは管理者・運用者。[[IADR-0152]] 決定 5）である。**
// 裁定が「読み取り口を 3 種類作らず、スコープだけロール別とする」と定めているので、
// **SC-05 のために別の口を作らない。**
//
// **SC-09 の `useTagDictionary` を import しない。** feature の外から触ってよいのは
// `index.ts` が再輸出したものだけ（Bulletproof React・計画 13_frontend-stack §基本方針）であり、
// **画面間で hook を貸し借りすると feature の境界が消える**。同じ生成フックを直接呼ぶ。
// [[IADR-0135]] 決定 1 と同じ作法: **orval 生成フック**で呼ぶ（手書き HTTP クライアントを持たない）。

/**
 * タグ辞書の値集合（名前の配列）。
 *
 * SC-05 が要るのは**選択肢**だけなので、使用件数（SC-09 の関心）は落として名前だけを返す。
 */
export function useTagOptions() {
  const query = useBffTagList<TagDictionaryResponse, unknown>({
    query: { queryKey: getBffTagListQueryKey(), select: okData },
  });

  // 既定値を残す（[[IADR-0132]] 決定 3）。契約上 `tags` は必須でも、実行時に本文を検証する層は無い。
  const names = query.data?.tags?.map((tag) => tag.name) ?? [];

  return {
    names,
    // 🔴 **取得できていないことと、辞書が空であることを呼び出し側が区別できるようにする。**
    // 区別できないと「読み込み中だから選べない」のか「辞書に 1 件も無い」のかが画面に出せず、
    // どちらも同じ無反応として見える。
    isPending: query.isPending,
    isError: query.isError,
  };
}
