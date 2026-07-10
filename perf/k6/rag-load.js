// NFR (#196): RAG 回答（AI 分析）の負荷試験（k6）。SLO: 初回応答 p95 ≤ 5s。
// 実行例:
//   BASE_URL=http://localhost:5000 TOKEN=<jwt> k6 run perf/k6/rag-load.js
// 注: /bff/analysis/ask は LLM ゲートウェイ経由（外部/セルフホスト）。実測は LLM 経路の構成に依存する。
//     ストリーミング初回応答（TTFB）を厳密に測る場合は /bff/analysis/ask/stream を対象に別途計測する。

import http from 'k6/http';
import { check, sleep } from 'k6';
import { BASE_URL, obtainToken, authHeaders, SEARCH_QUERIES } from './lib/config.js';

export const options = {
  scenarios: {
    // RAG は検索より重いため VU は控えめに。実測時に LLM 経路のスループットへ合わせる。
    rag: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '30s', target: 5 },
        { duration: '2m', target: 10 },
        { duration: '30s', target: 0 },
      ],
      exec: 'ask',
    },
  },
  thresholds: {
    // NFR: RAG 初回応答 p95 ≤ 5s。非ストリーミング応答全体を近似指標として計測する。
    'http_req_duration{scenario:rag}': ['p(95)<5000'],
    'http_req_failed{scenario:rag}': ['rate<0.02'],
  },
};

export function setup() {
  return { token: obtainToken() };
}

export function ask(data) {
  const question = SEARCH_QUERIES[Math.floor(Math.random() * SEARCH_QUERIES.length)];
  const res = http.post(
    `${BASE_URL}/bff/analysis/ask`,
    JSON.stringify({ question }),
    Object.assign({ tags: { scenario: 'rag' }, timeout: '30s' }, authHeaders(data.token)),
  );
  check(res, {
    'status is 200': (r) => r.status === 200,
  });
  sleep(2);
}
