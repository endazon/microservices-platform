// ADR-0031 / IADR-0262 決定 4: feature の公開面。
// Bulletproof React（計画 13_frontend-stack §基本方針）では、**feature の外から触ってよいのは
// このファイルが再輸出したものだけ**である。`api/` `components/` `hooks/` `routes/` `types/` へ
// feature の外から直接 import しない。
//
// SC-01 / SC-03 が使う「権限内 Wiki 台帳の索引」は本 feature ではなく `lib/wiki-pages` に在る
// （feature 同士は互いを import しない）。
export { createSc04WikiRoute, sc04WikiNav, sc04WikiBreadcrumb } from './routes/sc04WikiRoute';
