import { getBffUserLookupQueryKey, useBffUserLookup } from '@foundation/api/generated/users/users';
import { okArray } from '@foundation/api/orvalSelect';
import type { UserSummaryDto } from '@foundation/api/generated/bff.schemas';
import { useLookupTerm } from './lookupTerm';

// SC-19 主要素 3, FR-19, ADR-0098 決定 1 / IADR-0445: 共有先に指定する利用者を**表示名で**扱う読み口。
//
// 🔴 **［2026-09-13 / [[IADR-0451]] / #1455］表示名の解決（`useResolvedUsers`）はユニットの
// `lib/users` へ移した** —— SC-03 の個人資料の表示（計画 ADR-0102 決定 3）が同じ口を要り、
// feature 間の import は境界規則が止めるためである。**検索（`lookup`）は本画面だけの口なのでここに残す。**
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
