import { useQuery } from '@tanstack/react-query';
import { bffUserResolve } from '@foundation/api/generated/users/users';
import { okArray } from '@foundation/api/orvalSelect';

// FR-19, SC-03, SC-19, ADR-0098 決定 1, ADR-0100, 計画 ADR-0102 決定 3 / [[IADR-0451]] (#1455):
// **利用者名 → 表示名**の読み口。
//
// 🔴 **画面には表示名を出し、利用者名（識別子）は出さない**（ADR-0098 決定 1）。台帳も文書属性も
// 持つのは利用者名だけなので、**表示名は別に引く**。それが `resolve` の存在理由である。
//
// 🔴 **`lib/` に置くのは、SC-19（共有先の一覧）と SC-03（個人資料の所有者）の 2 画面が要るためである。**
// feature 間の import は境界規則（`import/no-restricted-paths`）が止める —— 共有するものは
// ユニットの `lib/` に置く（`lib/abac` / `lib/scope-filter` と同じ形。公開面は `index.ts` 1 枚）。
//
// **`resolve` は無効化済み（退職者）も返す** —— 既に台帳に載っている共有先や、退職者が所有する
// 個人資料の所有者を表示できなければ、取り消しも問い合わせもできなくなる。

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
