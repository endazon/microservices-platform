// ADR-0066 決定 1 / IADR-0308: 機密区分・部門・ライフサイクルは**画面ではなくドメイン語彙**である。
// 2 つ以上の画面（SC-05 / SC-06）が要るので `features/` ではなく `lib/` に置く。
//
// ADR-0066 決定 4 / IADR-0262 決定 4: **公開面はこのファイルだけ**である。
// 外から `./confidentiality` のような内部パスを直接 import しない。
export {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from './confidentiality';
export { DEPARTMENT_KEY, UNRESOLVED_DEPARTMENT } from './department';
export { DEFAULT_LIFECYCLE, LIFECYCLE_KEY, LIFECYCLE_VALUES } from './lifecycle';
export { UNRESOLVED_OWNER } from './owner';
