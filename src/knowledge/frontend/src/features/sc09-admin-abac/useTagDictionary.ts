import { useQueryClient } from '@tanstack/react-query';
import {
  getBffTagListQueryKey,
  useBffTagCreate,
  useBffTagDelete,
  useBffTagList,
  useBffTagRename,
} from '@foundation/api/generated/tags/tags';
import { okData } from '@foundation/api/orvalSelect';
import { ApiError } from '@foundation/api/ApiError';
import type { TagDictionaryResponse } from '@foundation/api/generated/bff.schemas';

// FR-09, SC-09, UC-05, #640: タグ辞書の照会と編集（サーバー状態は TanStack Query。ADR-0031）。
//
// **口は `/bff/tags`（#640 で新設）である。** #634 / #635 は DocumentService 側にしか置かず、
// 読み取りだけが `/bff/attribute-values` の `dictionary` へ相乗りしていたため、
// **画面は辞書を読めても編集できなかった**。
//
// [[IADR-0135]] 決定 1 と同じ作法: **orval 生成フック**で呼ぶ（手書き HTTP クライアントを持たない）。
// [[IADR-0127]] 決定 5 と同じ作法: 変更操作の成功後は `invalidateQueries` だけを行う。

export const tagDictionaryKey = getBffTagListQueryKey();

/** 辞書の一覧（値集合 ＋ 使用件数）。 */
export function useTagDictionary() {
  return useBffTagList<TagDictionaryResponse, unknown>({
    query: { queryKey: tagDictionaryKey, select: okData },
  });
}

/**
 * 辞書の編集（追加・改名・削除）。
 *
 * - 追加・改名は**名前の重複で 409**（SC-09「新しい名前は既存値と重複しない」）
 * - 削除は**参照が 1 件でもあれば 409**（SC-09。[[IADR-0153]] 決定 6）
 */
export function useTagActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: tagDictionaryKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffTagCreate<unknown>(onSuccess);
  const rename = useBffTagRename<unknown>(onSuccess);
  const remove = useBffTagDelete<unknown>(onSuccess);

  return { create, rename, remove };
}

/**
 * 削除拒否（409）に載っている使用件数を取り出す。
 *
 * **SC-09 は「削除前に使用件数を示す」と定めている。** サーバは
 * `{ error: "tag_in_use", message, usageCount }` を返し、BFF が本文ごと透過する。
 *
 * **`ApiError.details` からは取れない** —— あちらは文字列しか持たず、
 * **翻訳済みの文へ数値を差し込めない**（サーバの日本語をそのまま出すと en ロケールで日本語が混ざる）。
 * そのため `ApiError.body`（#640 で追加）から**数値として**読む。
 *
 * 該当しない失敗（404・502・そもそも別の競合）では `null` を返し、呼び出し側が既定の文言を出す。
 */
export function tagInUseCount(err: unknown): number | null {
  if (!(err instanceof ApiError) || err.kind !== 'conflict') return null;
  const body = err.body;
  if (typeof body !== 'object' || body === null) return null;
  const record = body as { error?: unknown; usageCount?: unknown };
  if (record.error !== 'tag_in_use') return null;
  return typeof record.usageCount === 'number' ? record.usageCount : null;
}
