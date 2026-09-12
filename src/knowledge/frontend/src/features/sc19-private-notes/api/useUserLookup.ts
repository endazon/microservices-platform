import { useQuery } from '@tanstack/react-query';
import {
  bffUserResolve,
  getBffUserLookupQueryKey,
  useBffUserLookup,
} from '@foundation/api/generated/users/users';
import { okArray } from '@foundation/api/orvalSelect';
import type { UserSummaryDto } from '@foundation/api/generated/bff.schemas';
import { useLookupTerm } from './lookupTerm';

// SC-19 主要素 3, FR-19, ADR-0098 決定 1 / IADR-0445: 共有先に指定する利用者を
// **表示名で**扱うための 2 本の読み口。
//
// 🔴 **画面には表示名を出し、利用者識別子は出さない**（ADR-0098 決定 1）。台帳が持つのは
// `subjectId`（利用者名）だけなので、**表示名は別に引く**。それが `resolve` の存在理由である。
//
// 🔴 **`lookup` は有効な利用者しか返さない**（退職者へは共有できない）が、**`resolve` は
// 無効化済みも返す** —— 既に台帳に載っている共有先を表示できなければ、取り消しもできなくなる。
// この非対称は契約側の意図であり、画面で揃えない。

/**
 * 表示名・利用者名の部分一致で候補を引く（有効な利用者のみ・表示名順）。
 *
 * **2 文字未満では問い合わせない**（下限とデバウンスは `lookupTerm.ts` が持つ）。
 */
export function useUserLookup(rawQuery: string) {
  const { term, enabled } = useLookupTerm(rawQuery);
  // 🔴 **問い合わせの結果を分配束縛（スプレッド）で混ぜない。** `@tanstack/query/no-rest-destructuring`
  // が禁じている形であり、TanStack Query の「読んだ項目だけを購読する」最適化が丸ごと外れる。
  // よって `query` は**そのまま 1 つの項目として返す**（`QueryState` へはこれを渡す）。
  return {
    enabled,
    query: useBffUserLookup<UserSummaryDto[], Error>(
      { q: term },
      { query: { queryKey: getBffUserLookupQueryKey({ q: term }), select: okArray, enabled } },
    ),
  };
}

/**
 * 利用者名の集合を表示名へ引く（見つからない名前は応答から落ちる）。
 *
 * IADR-0135 決定 2 と同じ作法: `/bff/users/resolve` は **POST** なので orval が生成するのは
 * `useMutation`（`useBffUserResolve`）であり、**照会としては使えない** —— キャッシュに載らず、
 * 一覧が無効化されるたびに画面側が発火を手で仕込む必要が生じる（`useEffect` で mutate する形は、
 * 「開き直すと表示名が一瞬消える」退行を作る）。そこで**生成された操作関数 `bffUserResolve` を
 * `useQuery` の `queryFn` に据える**。型も URL も生成物由来であり、出口も `bffFetch` →
 * `apiRequest` の一本道である（手書き HTTP クライアントではない）。
 *
 * **空集合では問い合わせない**（契約は空の `usernames` を 400 にする）。
 */
export function useResolvedUsers(usernames: readonly string[]) {
  // キーは**並び順に依存させない** —— 台帳の付与順が変わっても同じ集合なら同じ結果である。
  const sorted = [...usernames].sort();
  return useQuery({
    queryKey: ['bff', 'users', 'resolve', ...sorted],
    queryFn: ({ signal }) => bffUserResolve({ usernames: sorted }, { signal }),
    select: okArray,
    enabled: sorted.length > 0,
  });
}
