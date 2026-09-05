import { useQueryClient } from '@tanstack/react-query';
import {
  getBffEdgeTypeListQueryKey,
  useBffEdgeTypeCreate,
  useBffEdgeTypeDelete,
  useBffEdgeTypeList,
  useBffEdgeTypeRename,
} from '@foundation/api/generated/edge-types/edge-types';
import { okArray } from '@foundation/api/orvalSelect';
import { ApiError } from '@foundation/api/ApiError';
import type { EdgeTypeDto } from '@foundation/api/generated/bff.schemas';

// FR-17, SC-09, UC-05, ADR-0033 決定 3・9, INDEX 決定 18 (#1241): 辺の型辞書の照会と編集
// （サーバー状態は TanStack Query。ADR-0031）。
//
// 🔴 **口は `/bff/edge-types` である。`/bff/graph/edge-types` ではない。**
//
//   | 口 | 認可 | 使用件数 | 使う画面 |
//   | --- | --- | --- | --- |
//   | `/bff/graph/edge-types` | 認証のみ | **持たない** | SC-03 / SC-18 / SC-21（描画・型フィルタ） |
//   | `/bff/edge-types`（本モジュール） | admin ＋ operator | **持つ** | SC-09（辞書管理） |
//
// **描画用カタログを本辞書で置き換えてはならない** —— SC-18 の `useEdgeTypeCatalog` を
// こちらへ差し替えると、一般利用者が 403 になる。**逆も同じく壊れる**（件数が無ければ
// SC-09 の「削除前に使用件数を示す」が満たせない）。**2 つの口は用途ごと別物である。**
//
// [[IADR-0135]] 決定 1 と同じ作法: **orval 生成フック**で呼ぶ（手書き HTTP クライアントを持たない）。
// [[IADR-0127]] 決定 5 と同じ作法: 変更操作の成功後は `invalidateQueries` だけを行う。

// **export しない。** 本モジュールの外から使う先が無く、
// 出すと Knip のラチェット（未使用 export の床）を 1 件押し上げる。
// タグ辞書の同名の定数は export されているが、**それは既存の負債であって手本ではない**。
const edgeTypeDictionaryKey = getBffEdgeTypeListQueryKey();

/**
 * 辞書の一覧（値集合 ＋ 使用件数）。
 *
 * **`okArray` を使う**（`okData` ではない）—— 応答が配列であり、`bffFetch` は 204 で `{}` を返すため
 * `okData(res) ?? []` は発火しない（`orvalSelect.ts` の注記）。
 */
export function useEdgeTypeDictionary() {
  return useBffEdgeTypeList<EdgeTypeDto[], unknown>({
    query: { queryKey: edgeTypeDictionaryKey, select: okArray },
  });
}

/**
 * 辞書の編集（追加・改名・削除）。
 *
 * - 追加・改名は**名前の重複で 409**
 * - 削除は**参照が 1 件でもあれば 409**（ADR-0033 決定 9 / INDEX 決定 18）
 * - **改名は既存の辺へ自動的に追随する** —— 辺は型 ID を参照しており、表示名は辞書で解決する。
 *   **画面側で辺を数え直したり書き換えたりしない**（正本を 2 つにしない）。
 */
export function useEdgeTypeActions() {
  const queryClient = useQueryClient();
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: edgeTypeDictionaryKey });
  const onSuccess = { mutation: { onSuccess: invalidate } };

  const create = useBffEdgeTypeCreate<unknown>(onSuccess);
  const rename = useBffEdgeTypeRename<unknown>(onSuccess);
  const remove = useBffEdgeTypeDelete<unknown>(onSuccess);

  return { create, rename, remove };
}

/**
 * 削除拒否（409）に載っている使用件数を取り出す。
 *
 * **タグ辞書の `tagInUseCount` と同型である** —— INDEX 決定 18 が「同じ規則をタグ辞書にも適用する」と
 * 定めており、**規則が同じなら取り出し方も同じにする**（別々の書き方にすると、片方だけ壊れても気付けない）。
 *
 * **`ApiError.details` からは取れない** —— あちらは文字列しか持たず、**翻訳済みの文へ数値を差し込めない**
 * （サーバの日本語をそのまま出すと en ロケールで日本語が混ざる）。そのため `ApiError.body` から
 * **数値として**読む。
 *
 * 該当しない失敗（404・502・名前重複の 409）では `null` を返し、呼び出し側が既定の文言を出す。
 */
export function edgeTypeInUseCount(err: unknown): number | null {
  if (!(err instanceof ApiError) || err.kind !== 'conflict') return null;
  const body = err.body;
  if (typeof body !== 'object' || body === null) return null;
  const record = body as { error?: unknown; usageCount?: unknown };
  if (record.error !== 'edge_type_in_use') return null;
  return typeof record.usageCount === 'number' ? record.usageCount : null;
}
