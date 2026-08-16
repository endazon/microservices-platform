// テンプレート: feature の公開面。
//
// Bulletproof React（Feature First Architecture。計画 13_frontend-stack §基本方針）では、
// **feature の外から触ってよいのはこのファイルが再輸出したものだけ**である。
// `api/` `components/` `hooks/` `routes/` `types/` へ feature の外から直接 import しない。
export { createSampleRoute, sampleNav } from './routes/sampleRoute';
