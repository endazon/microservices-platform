import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  bffUserResolve,
  getBffUserLookupQueryKey,
  useBffUserLookup,
} from '@foundation/api/generated/users/users';
import { okArray } from '@foundation/api/orvalSelect';
import type { UserSummaryDto } from '@foundation/api/generated/bff.schemas';

// SC-19 主要素 3, FR-19, ADR-0098 決定 1 / IADR-0445: 共有先に指定する利用者を
// **表示名で**扱うための 2 本の読み口。
//
// 🔴 **画面には表示名を出し、利用者識別子は出さない**（ADR-0098 決定 1）。台帳が持つのは
// `subjectId`（利用者名）だけなので、**表示名は別に引く**。それが `resolve` の存在理由である。
//
// 🔴 **`lookup` は有効な利用者しか返さない**（退職者へは共有できない）が、**`resolve` は
// 無効化済みも返す** —— 既に台帳に載っている共有先を表示できなければ、取り消しもできなくなる。
// この非対称は契約側の意図であり、画面で揃えない。

/** 検索を始める最小文字数（契約 `q` の `minLength: 2`）。1 文字は 400 になる。 */
const MIN_QUERY_LENGTH = 2;

/** 入力の落ち着きを待つ時間。**1 文字ごとに問い合わせない**ための間隔である。 */
const DEBOUNCE_MS = 300;

/**
 * 値の変化を `delay` ミリ秒だけ遅らせて返す。
 *
 * **export しない** —— いまの用途は共有先の検索 1 つであり、汎用の口を作ると
 * 未使用 export の床（check-knip）を押し上げる。他画面が要るようになったら foundation へ出す。
 */
function useDebounced<T>(value: T, delay: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);
  return settled;
}

/**
 * 表示名・利用者名の部分一致で候補を引く（有効な利用者のみ・表示名順）。
 *
 * **2 文字未満では問い合わせない。** 契約が 400 を返す条件を画面側でも止める
 * （多層防御。`syncFolders.ts` と同じ考え方で、**値域の防壁はサーバ側にある**）。
 */
export function useUserLookup(rawQuery: string) {
  const term = useDebounced(rawQuery.trim(), DEBOUNCE_MS);
  const enabled = term.length >= MIN_QUERY_LENGTH;
  // 🔴 **問い合わせの結果を分配束縛（スプレッド）で混ぜない。** `@tanstack/query/no-rest-destructuring`
  // が禁じている形であり、TanStack Query の「読んだ項目だけを購読する」最適化が丸ごと外れる。
  // よって `query` は**そのまま 1 つの項目として返す**（`QueryState` へはこれを渡す）。
  return {
    /** 入力が落ち着いて、かつ 2 文字以上になったか（画面の案内文の出し分けに使う）。 */
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
