import { useQueries } from '@tanstack/react-query';
import { bffAttributeValues } from '@foundation/api/generated/search/search';
import { okData } from '@foundation/api/orvalSelect';
import { SCOPE_AXES, type ScopeAxis } from './scopeSelection';

// FR-04, FR-05, SC-01, SC-08, #539 / #540: 対象範囲の**候補**を引く。
//
// **候補は権限内に限る**（計画 §SC-01「権限内のタグ／`department`／`project` のみ選択可」）。
// 絞り込みは口の側で行われる——`POST /bff/attribute-values` は
// **「利用者が到達できる文書に、実際に付与されている値の集合のみ」**を返し、
// **件数は返さない**（ADR-0043 決定 1・2 / [[IADR-0151]]）。
// **辞書を丸ごと返さない**のは、権限外の文書に固有の値からその存在が推測できてしまうためである。
//
// **画面はここへ何も足さない。** 候補の絞り込みはサーバ側の責務であり、
// クライアントで補うと存在秘匿の境界が 2 か所に割れる。
//
// **［#641 レビュー 🟡 の是正］サーバー状態は TanStack Query に一元化する**
// （`CLAUDE.md`「サーバー状態」。`queryClient.ts` が唯一の生成点）。
// 当初 `useEffect` ＋ `useState` で直接呼んでいたが、**規約からの無記録の逸脱**であり、
// **`ScopeFilter` は SC-01 / SC-08 の共有部品なので、マウントのたびに 3 軸を取り直していた**。
//
// **`/bff/attribute-values` は POST なので orval が生成するのは `useMutation` である**
// （`useBffAttributeValues`）。照会には使えない——キャッシュに載らない。
// そこで `useSearchQuery`（SC-02）と同じ作法で、**生成された操作関数を `useQueries` の
// `queryFn` に据える**（[[IADR-0135]] 決定 2）。出口は `bffFetch` → `apiRequest` の一本道であり、
// 手書き HTTP クライアントではない（[[IADR-0121]] 決定 3）。

/** 軸ごとの候補。未取得・取得失敗の軸は空配列になる。 */
export type ScopeCandidates = Record<ScopeAxis, string[]>;

/** キャッシュキー。**軸を含める**——3 軸は別々の要求であり、別々にキャッシュされるべきである。 */
export const scopeCandidatesQueryKey = (axis: ScopeAxis) =>
  ['bff', 'attribute-values', axis] as const;

/**
 * 3 軸の候補をまとめて引く。
 *
 * **失敗した軸は空配列へ縮退させ、画面全体を落とさない**——対象範囲は任意の絞り込みであり、
 * 候補が引けないことは「検索・分析ができない」ことを意味しない。
 * `useQueries` なので**軸ごとに独立して成功・失敗する**（1 軸の失敗が他を巻き込まない）。
 */
export function useScopeCandidates(): { candidates: ScopeCandidates; loading: boolean } {
  const results = useQueries({
    queries: SCOPE_AXES.map((axis) => ({
      queryKey: scopeCandidatesQueryKey(axis),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        bffAttributeValues({ key: axis }, { signal }),
      // **`select` の引数型を明示する。** `useQueries` は配列を map で組み立てると
      // 封筒型の推論が落ちる（実測: `okData` の戻りが `never` になる）。
      // `orvalSelect.ts` が「呼び出し側は本文型を明示する」と書いているのと同じ理由である。
      select: (res: Awaited<ReturnType<typeof bffAttributeValues>>) => okData(res),
      // 候補は人が管理する値集合であり、1 回の画面操作の間に変わらない。
      // **再取得で候補が入れ替わると、利用者が選んでいる最中にチップが消える。**
      staleTime: 5 * 60 * 1000,
    })),
  });

  const candidates = Object.fromEntries(
    SCOPE_AXES.map((axis, i) => [axis, results[i]?.data?.values ?? []]),
  ) as ScopeCandidates;

  return { candidates, loading: results.some((r) => r.isLoading) };
}
