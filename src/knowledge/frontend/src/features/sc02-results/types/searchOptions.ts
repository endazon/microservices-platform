import { msg } from '@lingui/core/macro';
import type { MessageDescriptor } from '@lingui/core';

// SC-02, UC-01, FR-03 / ADR-0031（hi-fi モック sc-02 のツールバー行「並び: 関連度 ▾」
// 「検索モード: ハイブリッド ▾」）: **検索モードと並び順の語彙**（純関数・純データ）。
//
// 契約は既に 3 値 / 2 値を持つ（`SearchRequest.mode` / `SearchRequest.sortBy`。裁定 Q4 / Q5）。
// **本ファイルが埋めるのは切替 UI の側だけ**であり、値域は契約（`bff.schemas.ts` の
// doc コメント）が正本である。
//
// 🔴 **既定値は URL にも要求本文にも載せない。** 契約は「未知の値は hybrid（relevance）として
// 扱う」と定めており、既定を省いた要求と既定を明示した要求は**サーバから見て同値**である。
// 省く側に寄せることで、(1) `/search?q=経費` という既存の URL の形が変わらず、
// (2) 既定のまま検索したときの要求本文が `{ query, topK }` のまま保たれる
// （既存の導線テストが固定している形である）。

/** 検索モードの 3 値（`SearchRequest.mode`）。 */
export const SEARCH_MODES = ['hybrid', 'keyword', 'semantic'] as const;
export type SearchMode = (typeof SEARCH_MODES)[number];
export const DEFAULT_SEARCH_MODE: SearchMode = 'hybrid';

/** 並び順の 2 値（`SearchRequest.sortBy`）。 */
export const SEARCH_SORTS = ['relevance', 'updated'] as const;
export type SearchSort = (typeof SEARCH_SORTS)[number];
export const DEFAULT_SEARCH_SORT: SearchSort = 'relevance';

const MODE_LABELS: Record<SearchMode, MessageDescriptor> = {
  hybrid: msg`ハイブリッド`,
  keyword: msg`キーワード`,
  semantic: msg`意味`,
};

const SORT_LABELS: Record<SearchSort, MessageDescriptor> = {
  relevance: msg`関連度`,
  updated: msg`更新日時の新しい順`,
};

/** 表示名。`MessageDescriptor` のまま返し、**描画時に**解決する（ロケール切替に追随させる）。 */
export function searchModeLabel(mode: SearchMode): MessageDescriptor {
  return MODE_LABELS[mode];
}

export function searchSortLabel(sort: SearchSort): MessageDescriptor {
  return SORT_LABELS[sort];
}

/**
 * URL の生値を検索モードへ正規化する。
 *
 * **既定と未知はいずれも `undefined`** へ倒す（URL へ既定を書き戻さないため）。
 * 未知の値を既定として扱うのは契約の定めそのものである。
 */
export function normalizeMode(raw: unknown): SearchMode | undefined {
  const known = SEARCH_MODES.find((m) => m === raw);
  return known === undefined || known === DEFAULT_SEARCH_MODE ? undefined : known;
}

export function normalizeSort(raw: unknown): SearchSort | undefined {
  const known = SEARCH_SORTS.find((s) => s === raw);
  return known === undefined || known === DEFAULT_SEARCH_SORT ? undefined : known;
}

/**
 * SC-02 の検索パラメータ（ルートの `validateSearch` の戻り値）。
 * **検索語・検索モード・並び順の単一情報源**である（IADR-0126 決定 3 の拡張）。
 */
export interface ResultsSearch {
  q: string;
  mode?: SearchMode;
  sort?: SearchSort;
}
