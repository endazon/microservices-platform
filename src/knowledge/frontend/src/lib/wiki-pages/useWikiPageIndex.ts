import {
  getBffWikiPageListQueryKey,
  useBffWikiPageList,
} from '@foundation/api/generated/wiki/wiki';
import type { bffWikiPageListResponse } from '@foundation/api/generated/wiki/wiki';
import { okArray } from '@foundation/api/orvalSelect';

// FR-13, UC-07, SC-01, SC-03, SC-04, ADR-0073 / IADR-0365 決定 1 (#1200): **権限内の Wiki 台帳の索引**。
//
// SC-01 の出典が Wiki 由来か（📖）、SC-03 に「Wiki で閲覧」を出すかは、従前 `sourceUri` が
// 実行時 config `wikiBaseUrl` で始まるかで判定していた。ADR-0073 決定 1 が stg/prod で
// `WIKI_BASE_URL` を**設定しない**と定めたため、その判定は本番で**一度も真にならない**（#1200 実測 4）。
//
// ここでは判定の根拠を **Wiki 台帳（`GET /bff/wiki/pages`）に文書 ID が載っているか**へ移す。
// 一覧は後段 WikiService が ABAC（deny-by-default の属性フィルタ）を通した**権限内のメタデータだけ**を
// 返す（[[IADR-0355]] 決定 5 で BFF は透過）ので、「載っている ＝ 利用者が SC-04 で開ける」が成り立つ。
//
// - **by-doc を存在判定に使わない**: 本文（HTML）ごと返る面であり、SC-01 では出典 N 件ぶんの往復になる。
//   一覧は 1 回で、SC-04 のページツリーと**同じ生成キー**（`['/bff/wiki/pages']`）なのでキャッシュを共有する。
// - **feature 跨ぎの共有は `lib/` に置く**（ADR-0066 決定 1 / [[IADR-0308]] 決定 6 の `scope-filter` と同じ判断）。
//   feature 同士は互いを import しない（[[IADR-0262]] 決定 4）。
// - **未取得・取得失敗は `undefined`** —— 呼び出し側は「Wiki かもしれない」を推測せず文書として扱う
//   （到達できない導線へ送らない。従前の `wikiBaseUrl` 未設定時と同じ倒し方）。

/** `select` は参照が安定していないと描画ごとに走る。モジュール定数にしておく。 */
function toDocumentIdSet(res: bffWikiPageListResponse): ReadonlySet<string> {
  return new Set(okArray(res).map((p) => p.documentId));
}

/** 戻り値の形。feature の外へ型として輸出しない（使う側は無く、未使用 export の床を押し上げる）。 */
interface WikiPageIndex {
  /** 権限内の Wiki ページを持つ文書 ID の集合。**未取得・取得失敗は `undefined`**。 */
  documentIds: ReadonlySet<string> | undefined;
  isPending: boolean;
}

export function useWikiPageIndex(): WikiPageIndex {
  const query = useBffWikiPageList<ReadonlySet<string>, unknown>({
    query: { queryKey: getBffWikiPageListQueryKey(), select: toDocumentIdSet },
  });
  return { documentIds: query.data, isPending: query.isPending };
}
