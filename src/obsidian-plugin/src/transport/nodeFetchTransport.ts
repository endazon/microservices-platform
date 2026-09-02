// FR-20, [[IADR-0338]] 決定 6: Node ハーネス（`cli/pull.ts`）用の HttpTransport。
// Obsidian 本体を持たない環境（CI・実測）で、同じ SyncClient / runPullSync を実 HTTP に当てる。
//
// ESLint の `no-restricted-globals: fetch`（SPA → BFF 境界の規則）は本ディレクトリだけ外してある
// （src/eslint.config.js）。プラグインは SPA ではなく BFF も経由しない——HTTP の出口はこのファイルと
// obsidianTransport.ts の 2 つに限る。
import type { HttpTransport } from '../protocol/transport.ts';

export const nodeFetchTransport: HttpTransport = async (request) => {
  const response = await fetch(request.url, { method: request.method, headers: request.headers });
  return { status: response.status, text: await response.text() };
};
