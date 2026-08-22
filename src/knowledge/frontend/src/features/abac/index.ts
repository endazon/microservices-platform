// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// Bulletproof React（計画 13_frontend-stack §基本方針）では、**feature の外から触ってよいのは
// このファイルが再輸出したものだけ**である。`api/` `components/` `hooks/` `routes/` `types/` へ
// feature の外から直接 import しない。
export {
  CONFIDENTIALITY_KEY,
  CONFIDENTIALITY_VALUES,
  DEFAULT_CONFIDENTIALITY,
} from './types/confidentiality';
export { DEPARTMENT_KEY, UNRESOLVED_DEPARTMENT } from './types/department';
export { DEFAULT_LIFECYCLE, LIFECYCLE_KEY, LIFECYCLE_VALUES } from './types/lifecycle';
