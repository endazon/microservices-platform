// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// Bulletproof React（計画 13_frontend-stack §基本方針）では、**feature の外から触ってよいのは
// このファイルが再輸出したものだけ**である。`api/` `components/` `routes/` `types/` へ
// feature の外から直接 import しない。
export { createSc18GraphRoute, sc18GraphNav, sc18GraphBreadcrumb } from './routes/sc18GraphRoute';
