import { useEffect, useState } from 'react';
import { bffAttributeValues } from '@foundation/api/generated/search/search';
import { SCOPE_AXES, type ScopeAxis } from './scopeFilter';

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

/** 軸ごとの候補。未取得・取得失敗の軸は空配列になる。 */
export type ScopeCandidates = Record<ScopeAxis, string[]>;

const EMPTY: ScopeCandidates = { tags: [], department: [], project: [] };

/**
 * 3 軸の候補をまとめて引く。
 *
 * **失敗した軸は空配列へ縮退させ、画面全体を落とさない**——対象範囲は任意の絞り込みであり、
 * 候補が引けないことは「検索・分析ができない」ことを意味しない。
 * **`useMutation` を 3 本並べない**（生成フックは mutation なので、初回表示で自動的には走らない）。
 */
export function useScopeCandidates(): { candidates: ScopeCandidates; loading: boolean } {
  const [candidates, setCandidates] = useState<ScopeCandidates>(EMPTY);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      const results = await Promise.all(
        SCOPE_AXES.map(async (axis) => {
          try {
            const res = await bffAttributeValues({ key: axis });
            return [axis, res.data.values ?? []] as const;
          } catch {
            // 軸ごとに独立して縮退する（1 軸の失敗で他の軸の候補まで失わない）。
            return [axis, [] as string[]] as const;
          }
        }),
      );
      if (cancelled) return;
      setCandidates(Object.fromEntries(results) as ScopeCandidates);
      setLoading(false);
    };

    void load();
    return () => {
      cancelled = true;
    };
  }, []);

  return { candidates, loading };
}
