// ADR-0066 決定 1 / IADR-0307: 対象範囲の絞り込み条件は**画面ではなくドメイン語彙**である。
// 2 つ以上の画面（SC-01 / SC-08）が要るので `features/` ではなく `lib/` に置く。
//
// ADR-0066 決定 4 / IADR-0262 決定 4: **公開面はこのファイルだけ**である。
// 外から `./scopeSelection` のような内部パスを直接 import しない。
export { ScopeFilter } from './ScopeFilter';
export { EMPTY_SELECTION, toAttributeFilters } from './scopeSelection';
export type { ScopeSelection } from './scopeSelection';
