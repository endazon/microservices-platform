import { useQuery } from '@tanstack/react-query';
import {
  bffGroupResolve,
  getBffGroupLookupQueryKey,
  useBffGroupLookup,
} from '@foundation/api/generated/groups/groups';
import { okArray } from '@foundation/api/orvalSelect';
import type { GroupSummaryDto } from '@foundation/api/generated/bff.schemas';
import { useLookupTerm } from './lookupTerm';

// SC-19 主要素 3, FR-19, ADR-0098 決定 1 / IADR-0447 (#1447): 共有先に指定するグループを
// **表示名で**扱うための 2 本の読み口（`useUserLookup.ts` と同型）。
//
// 🔴 **画面には表示名とパスを出し、グループ識別子（Keycloak のグループ ID）は出さない**
// （ADR-0098 決定 1）。台帳が持つのは `subjectId`（＝グループ ID）だけなので、
// **表示名は別に引く**。それが `resolve` の存在理由である。
//
// 🔴 **`path` は「同名のグループを区別する」ためだけに添える。** 識別子ではないので出してよいが、
// 主たる手掛かりは `displayName` である（パスを主に出すと、木の形を知らない利用者には読めない）。
//
// 🔴 **`resolve` は無い ID を落とす**（`lookup` が有効なグループだけを返すのと同じ非対称ではない）。
// 管理者が Keycloak でグループを消しても台帳の行は残るため、**引けなかった ID の行は消さない**
// （消すと取り消せなくなり、「見えない共有」が恒久化する）。画面側の文言は
// `ShareTargetsDialog` が持つ。

/**
 * グループ名の部分一致で候補を引く（グループ木を平坦化・パス順）。
 *
 * **2 文字未満では問い合わせない**（下限とデバウンスは `lookupTerm.ts` が持つ）。
 */
export function useGroupLookup(rawQuery: string) {
  const { term, enabled } = useLookupTerm(rawQuery);
  // 🔴 **問い合わせの結果を分配束縛（スプレッド）で混ぜない**（`useUserLookup` と同じ理由。
  // `@tanstack/query/no-rest-destructuring` が禁じている形である）。
  return {
    enabled,
    query: useBffGroupLookup<GroupSummaryDto[], Error>(
      { q: term },
      { query: { queryKey: getBffGroupLookupQueryKey({ q: term }), select: okArray, enabled } },
    ),
  };
}

/**
 * グループ ID の集合を表示名へ引く（無い ID は応答から落ちる）。
 *
 * `useResolvedUsers` と同じ作法（IADR-0135 決定 2）: `/bff/groups/resolve` は **POST** だが
 * **照会**であり、生成された操作関数を `useQuery` の `queryFn` に据える（`useMutation` では
 * キャッシュに載らず、一覧の無効化ごとに発火を手で仕込む形になる）。
 *
 * **空集合では問い合わせない**（契約は空の `ids` を 400 にする）。
 */
export function useResolvedGroups(ids: readonly string[]) {
  // キーは**並び順に依存させない** —— 台帳の付与順が変わっても同じ集合なら同じ結果である。
  const sorted = [...ids].sort();
  return useQuery({
    queryKey: ['bff', 'groups', 'resolve', ...sorted],
    queryFn: ({ signal }) => bffGroupResolve({ ids: sorted }, { signal }),
    select: okArray,
    enabled: sorted.length > 0,
  });
}
